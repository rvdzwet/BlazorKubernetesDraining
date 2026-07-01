using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BlazorKubernetesDraining;

/// <summary>
/// Scoped CircuitHandler that orchestrates state preservation across pod failovers.
///
/// Performance Architecture:
/// - OnCircuitOpenedAsync: ONE batched Redis read per session. Pre-loads all [PersistState] member
///   values into the singleton CircuitStateCache. Subsequent component activations read from memory.
/// - OnConnectionDownAsync: ONE batched Redis write per session. Serializes all tracked components
///   and DI services with [PersistState] members in a single pass.
/// - OnCircuitClosedAsync: Evicts the session from the CircuitStateCache to prevent memory leaks.
///
/// Scoping:
/// - This handler is registered as Scoped and correctly resolves scoped DI services
///   (SessionIdentityProvider, IScopedStateServiceRegistration, ScopedComponentStateRegistry).
/// </summary>
public class PersistStateCircuitHandler : CircuitHandler
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ScopedComponentStateRegistry _componentRegistry;
    private readonly CircuitStateCache _stateCache;
    private readonly SessionIdentityProvider _sessionProvider;
    private readonly IDistributedCache? _cache;
    private readonly StatePreservationOptions _options;
    private readonly ILogger<PersistStateCircuitHandler>? _logger;

    public PersistStateCircuitHandler(
        IServiceProvider serviceProvider,
        ScopedComponentStateRegistry componentRegistry,
        CircuitStateCache stateCache,
        SessionIdentityProvider sessionProvider,
        IOptions<StatePreservationOptions> options,
        ILogger<PersistStateCircuitHandler>? logger = null)
    {
        _serviceProvider = serviceProvider;
        _componentRegistry = componentRegistry;
        _stateCache = stateCache;
        _sessionProvider = sessionProvider;
        _options = options.Value;
        _logger = logger;
        _cache = _serviceProvider.GetService<IDistributedCache>();
    }

    /// <summary>
    /// Pre-loads ALL [PersistState] data from Redis into the in-memory CircuitStateCache
    /// and rehydrates Scoped DI services. Runs ONCE per circuit, BEFORE components render.
    /// </summary>
    public override async Task OnCircuitOpenedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        if (!_options.EnableStatePreservation || _cache == null || _sessionProvider.SessionId == null)
        {
            await base.OnCircuitOpenedAsync(circuit, cancellationToken);
            return;
        }

        var sessionId = _sessionProvider.SessionId;

        try
        {
            // 1. Load component state snapshot from Redis (ONE read operation)
            var componentSnapshotKey = $"{_options.RedisKeyPrefix}:{sessionId}:Components";
            var componentJson = await _cache.GetStringAsync(componentSnapshotKey, cancellationToken);

            if (!string.IsNullOrEmpty(componentJson))
            {
                var snapshot = JsonSerializer.Deserialize<Dictionary<string, string>>(componentJson, StateSerializationConfig.Options);
                if (snapshot != null)
                {
                    _stateCache.StoreSnapshot(sessionId, snapshot);

                    if (_options.EnableDiagnostics)
                    {
                        _logger?.LogInformation("Pre-loaded {Count} [PersistState] member values from Redis for session [{Session}].",
                            snapshot.Count, sessionId);
                    }
                }
            }

            // 2. Rehydrate Scoped DI services directly (they exist in the scoped container now)
            var diSnapshotKey = $"{_options.RedisKeyPrefix}:{sessionId}:DI";
            var diJson = await _cache.GetStringAsync(diSnapshotKey, cancellationToken);

            if (!string.IsNullOrEmpty(diJson))
            {
                var diSnapshot = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(diJson, StateSerializationConfig.Options);
                if (diSnapshot != null)
                {
                    var stateServices = _serviceProvider.GetServices<IScopedStateServiceRegistration>();
                    foreach (var reg in stateServices)
                    {
                        var members = ComponentStateClassifier.GetPersistedMembers(reg.ServiceType);
                        foreach (var member in members)
                        {
                            try
                            {
                                var key = $"{reg.ServiceType.FullName}:{member.Name}";
                                if (diSnapshot.TryGetValue(key, out var element))
                                {
                                    var targetType = member is PropertyInfo p ? p.PropertyType : ((FieldInfo)member).FieldType;
                                    var val = JsonSerializer.Deserialize(element, targetType, StateSerializationConfig.Options);

                                    if (member is PropertyInfo prop) prop.SetValue(reg.Instance, val);
                                    else ((FieldInfo)member).SetValue(reg.Instance, val);
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger?.LogWarning(ex, "Failed to rehydrate [{Member}] on DI service [{Service}]. Skipping.",
                                    member.Name, reg.ServiceType.FullName);
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error pre-loading state from Redis for session [{Session}].", sessionId);
        }

        await base.OnCircuitOpenedAsync(circuit, cancellationToken);
    }

    /// <summary>
    /// Checkpoints all [PersistState] members on tracked components and DI services to Redis
    /// in TWO batched writes (one for components, one for DI services).
    /// </summary>
    public override async Task OnConnectionDownAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        if (!_options.EnableStatePreservation || _cache == null || _sessionProvider.SessionId == null)
        {
            await base.OnConnectionDownAsync(circuit, cancellationToken);
            return;
        }

        var sessionId = _sessionProvider.SessionId;
        var cacheOptions = new DistributedCacheEntryOptions { SlidingExpiration = _options.SlidingExpiration };

        try
        {
            // 1. Batch checkpoint all tracked Razor components into ONE Redis write
            var componentSnapshot = new Dictionary<string, string>();
            foreach (var (componentId, instance, members) in _componentRegistry.GetTrackedComponents())
            {
                foreach (var member in members)
                {
                    try
                    {
                        var val = member is PropertyInfo p ? p.GetValue(instance) : ((FieldInfo)member).GetValue(instance);
                        var key = $"Component:{componentId}:{member.Name}";
                        componentSnapshot[key] = JsonSerializer.Serialize(val, StateSerializationConfig.Options);
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogDebug(ex, "Could not read [{Member}] on component [{Component}].", member.Name, componentId);
                    }
                }
            }

            if (componentSnapshot.Count > 0)
            {
                var json = JsonSerializer.Serialize(componentSnapshot, StateSerializationConfig.Options);
                await _cache.SetStringAsync($"{_options.RedisKeyPrefix}:{sessionId}:Components", json, cacheOptions, cancellationToken);
            }

            // 2. Batch checkpoint all Scoped DI services into ONE Redis write
            var diSnapshot = new Dictionary<string, object?>();
            var stateServices = _serviceProvider.GetServices<IScopedStateServiceRegistration>();
            foreach (var reg in stateServices)
            {
                var members = ComponentStateClassifier.GetPersistedMembers(reg.ServiceType);
                foreach (var member in members)
                {
                    try
                    {
                        var val = member is PropertyInfo p ? p.GetValue(reg.Instance) : ((FieldInfo)member).GetValue(reg.Instance);
                        diSnapshot[$"{reg.ServiceType.FullName}:{member.Name}"] = val;
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogDebug(ex, "Could not read [{Member}] on DI service [{Service}].", member.Name, reg.ServiceType.FullName);
                    }
                }
            }

            if (diSnapshot.Count > 0)
            {
                var json = JsonSerializer.Serialize(diSnapshot, StateSerializationConfig.Options);
                await _cache.SetStringAsync($"{_options.RedisKeyPrefix}:{sessionId}:DI", json, cacheOptions, cancellationToken);
            }

            if (_options.EnableDiagnostics)
            {
                _logger?.LogInformation("Checkpointed {Components} component members and {DI} DI members to Redis for session [{Session}].",
                    componentSnapshot.Count, diSnapshot.Count, sessionId);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error checkpointing state to Redis for session [{Session}].", sessionId);
        }

        await base.OnConnectionDownAsync(circuit, cancellationToken);
    }

    /// <summary>
    /// Evicts the session's pre-loaded state from the singleton CircuitStateCache to prevent
    /// unbounded memory growth in long-running pods.
    /// </summary>
    public override Task OnCircuitClosedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        if (_sessionProvider.SessionId != null)
        {
            _stateCache.EvictSession(_sessionProvider.SessionId);
        }

        return base.OnCircuitClosedAsync(circuit, cancellationToken);
    }
}
