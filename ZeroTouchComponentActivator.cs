using System;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BlazorKubernetesDraining;

/// <summary>
/// Replaces Microsoft's default IComponentActivator to rehydrate [PersistState] members
/// from an in-memory cache during component creation.
///
/// Performance Architecture:
/// - This activator does NOT call Redis. It reads from the singleton CircuitStateCache, which
///   was pre-populated by PersistStateCircuitHandler.OnCircuitOpenedAsync (one Redis call per circuit).
/// - Components with zero [PersistState] members are short-circuited immediately (no overhead).
/// - Dictionary lookups per [PersistState] member: O(1) average, zero network I/O.
///
/// Session Identity:
/// - The activator resolves the session ID from a scoped SessionIdentityProvider via the
///   service provider. This avoids the captive dependency anti-pattern: the activator is a
///   singleton but reads the session ID through a scoped accessor, not by capturing scoped state.
/// </summary>
public class PersistStateComponentActivator : IComponentActivator
{
    private readonly IServiceProvider _serviceProvider;
    private readonly CircuitStateCache _stateCache;
    private readonly StatePreservationOptions _options;
    private readonly ILogger<PersistStateComponentActivator>? _logger;

    public PersistStateComponentActivator(
        IServiceProvider serviceProvider,
        CircuitStateCache stateCache,
        IOptions<StatePreservationOptions> options,
        ILogger<PersistStateComponentActivator>? logger = null)
    {
        _serviceProvider = serviceProvider;
        _stateCache = stateCache;
        _options = options.Value;
        _logger = logger;
    }

    public IComponent CreateInstance(Type componentType)
    {
        // 1. Create component instance (standard Blazor behavior)
        var instance = (IComponent)Activator.CreateInstance(componentType)!;

        if (!_options.EnableStatePreservation)
        {
            return instance;
        }

        // 2. Fast path: skip components with zero [PersistState] members (majority of components)
        var members = ComponentStateClassifier.GetPersistedMembers(componentType);
        if (members.Length == 0)
        {
            return instance;
        }

        try
        {
            // 3. Register for checkpointing on connection drop
            var registry = _serviceProvider.GetService<ScopedComponentStateRegistry>();
            var componentId = componentType.FullName!;
            registry?.Register(componentId, instance, members);

            // 4. Resolve session ID from the scoped provider
            var sessionProvider = _serviceProvider.GetService<SessionIdentityProvider>();
            var sessionId = sessionProvider?.SessionId;

            if (sessionId != null)
            {
                // 5. Rehydrate from in-memory cache (populated by CircuitHandler, NOT from Redis)
                int rehydrated = 0;
                foreach (var member in members)
                {
                    try
                    {
                        var cacheKey = $"Component:{componentId}:{member.Name}";
                        var json = _stateCache.GetMemberValue(sessionId, cacheKey);

                        if (json != null)
                        {
                            var targetType = member is PropertyInfo p ? p.PropertyType : ((FieldInfo)member).FieldType;
                            var val = JsonSerializer.Deserialize(json, targetType, StateSerializationConfig.Options);

                            if (member is PropertyInfo prop) prop.SetValue(instance, val);
                            else ((FieldInfo)member).SetValue(instance, val);

                            rehydrated++;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogWarning(ex, "Failed to rehydrate [{Member}] on [{Component}]. Skipping.",
                            member.Name, componentId);
                    }
                }

                if (_options.EnableDiagnostics && rehydrated > 0)
                {
                    _logger?.LogDebug("Rehydrated {Count}/{Total} [PersistState] members on [{Component}] from cache.",
                        rehydrated, members.Length, componentId);
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error during state rehydration for [{Component}]. Proceeding with defaults.",
                componentType.FullName);
        }

        return instance;
    }
}
