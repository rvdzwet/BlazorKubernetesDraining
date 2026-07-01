using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BlazorKubernetesDraining;

/// <summary>
/// Universal CircuitHandler that automatically synchronizes in-memory domain state between ASP.NET Core RAM
/// and Redis Cluster across Kubernetes pod failovers.
/// 
/// Coverage:
/// 1. Scoped Dependency Injected Services (registered via AddScopedState).
/// 2. Active Razor Components (tracked via ScopedComponentStateRegistry with WeakReferences).
/// 
/// Resiliency Features:
/// - Uses StateSerializationConfig (circular reference handling, tolerant reading).
/// - Isolated Try/Catch blocks per service and component to prevent cascading failures during checkpointing.
/// </summary>
public class UniversalZeroTouchCircuitHandler : CircuitHandler
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ScopedComponentStateRegistry _componentRegistry;
    private readonly IDistributedCache? _cache;
    private readonly IHttpContextAccessor? _httpContext;
    private readonly StatePreservationOptions _options;
    private readonly ILogger<UniversalZeroTouchCircuitHandler>? _logger;

    public UniversalZeroTouchCircuitHandler(
        IServiceProvider serviceProvider,
        ScopedComponentStateRegistry componentRegistry,
        IOptions<StatePreservationOptions> options,
        ILogger<UniversalZeroTouchCircuitHandler>? logger = null)
    {
        _serviceProvider = serviceProvider;
        _componentRegistry = componentRegistry;
        _options = options.Value;
        _logger = logger;
        _cache = _serviceProvider.GetService<IDistributedCache>();
        _httpContext = _serviceProvider.GetService<IHttpContextAccessor>();
    }

    /// <summary>
    /// Invoked automatically when a Blazor circuit opens (or recovers on a newly deployed pod after failover).
    /// Rehydrates all registered Scoped DI state services before components render.
    /// </summary>
    public override async Task OnCircuitOpenedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        if (!_options.EnableStatePreservation || _cache == null)
        {
            await base.OnCircuitOpenedAsync(circuit, cancellationToken);
            return;
        }

        var sessionId = GetSessionId(circuit);
        var stateServices = _serviceProvider.GetServices<IScopedStateServiceRegistration>();

        foreach (var reg in stateServices)
        {
            try
            {
                var redisKey = $"{_options.RedisKeyPrefix}:{sessionId}:DI:{reg.ServiceType.FullName}";
                var json = await _cache.GetStringAsync(redisKey, cancellationToken);

                if (!string.IsNullOrEmpty(json))
                {
                    var stateDict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json, StateSerializationConfig.Options);
                    if (stateDict != null)
                    {
                        var members = ComponentStateClassifier.GetDomainStateMembers(reg.ServiceType);
                        foreach (var member in members)
                        {
                            try
                            {
                                if (stateDict.TryGetValue(member.Name, out var element))
                                {
                                    var targetType = member is PropertyInfo p ? p.PropertyType : ((FieldInfo)member).FieldType;
                                    var val = JsonSerializer.Deserialize(element, targetType, StateSerializationConfig.Options);

                                    if (member is PropertyInfo prop) prop.SetValue(reg.Instance, val);
                                    else ((FieldInfo)member).SetValue(reg.Instance, val);
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger?.LogWarning(ex, "Failed to rehydrate member [{Member}] on Scoped DI service [{Service}]. Skipping member.",
                                    member.Name, reg.ServiceType.FullName);
                            }
                        }

                        if (_options.EnableDiagnostics)
                        {
                            _logger?.LogInformation("Rehydrated Scoped DI service [{Service}] from Redis snapshot.", reg.ServiceType.FullName);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error rehydrating Scoped DI service [{Service}] from Redis.", reg.ServiceType.FullName);
            }
        }

        await base.OnCircuitOpenedAsync(circuit, cancellationToken);
    }

    /// <summary>
    /// Invoked automatically when a connection drops or when a pod is shutting down.
    /// Checkpoints both Scoped DI services and active Razor components to Redis Cluster.
    /// </summary>
    public override async Task OnConnectionDownAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        if (!_options.EnableStatePreservation || _cache == null)
        {
            await base.OnConnectionDownAsync(circuit, cancellationToken);
            return;
        }

        var sessionId = GetSessionId(circuit);
        var cacheOptions = new DistributedCacheEntryOptions { SlidingExpiration = _options.SlidingExpiration };

        // 1. Checkpoint Scoped DI Services
        var stateServices = _serviceProvider.GetServices<IScopedStateServiceRegistration>();
        foreach (var reg in stateServices)
        {
            try
            {
                var members = ComponentStateClassifier.GetDomainStateMembers(reg.ServiceType);
                var stateDict = new Dictionary<string, object?>();
                foreach (var member in members)
                {
                    try
                    {
                        var val = member is PropertyInfo p ? p.GetValue(reg.Instance) : ((FieldInfo)member).GetValue(reg.Instance);
                        stateDict[member.Name] = val;
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogDebug(ex, "Could not read member [{Member}] on DI service [{Service}] during checkpoint.", member.Name, reg.ServiceType.FullName);
                    }
                }

                var json = JsonSerializer.Serialize(stateDict, StateSerializationConfig.Options);
                await _cache.SetStringAsync($"{_options.RedisKeyPrefix}:{sessionId}:DI:{reg.ServiceType.FullName}", json, cacheOptions, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error checkpointing Scoped DI service [{Service}].", reg.ServiceType.FullName);
            }
        }

        // 2. Checkpoint Active Razor Components (WeakReferences are pruned automatically in GetTrackedComponents)
        foreach (var (componentId, instance, members) in _componentRegistry.GetTrackedComponents())
        {
            try
            {
                var stateDict = new Dictionary<string, object?>();
                foreach (var member in members)
                {
                    try
                    {
                        var val = member is PropertyInfo p ? p.GetValue(instance) : ((FieldInfo)member).GetValue(instance);
                        stateDict[member.Name] = val;
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogDebug(ex, "Could not read member [{Member}] on component [{Component}] during checkpoint.", member.Name, componentId);
                    }
                }

                var json = JsonSerializer.Serialize(stateDict, StateSerializationConfig.Options);
                await _cache.SetStringAsync($"{_options.RedisKeyPrefix}:{sessionId}:Component:{componentId}", json, cacheOptions, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error checkpointing component [{Component}].", componentId);
            }
        }

        if (_options.EnableDiagnostics)
        {
            _logger?.LogInformation("Checkpointed circuit [{CircuitId}] state to Redis.", circuit.Id);
        }

        await base.OnConnectionDownAsync(circuit, cancellationToken);
    }

    private string GetSessionId(Circuit circuit)
    {
        return _httpContext?.HttpContext?.User.FindFirst("SessionId")?.Value ??
               _httpContext?.HttpContext?.Connection.Id ??
               circuit.Id;
    }
}
