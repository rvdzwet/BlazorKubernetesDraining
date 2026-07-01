using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BlazorKubernetesDraining;

/// <summary>
/// Custom IComponentActivator that replaces Microsoft's default component activator.
/// 
/// Interception Mechanics:
/// 1. When Blazor's render engine instantiates any Razor component, CreateInstance is invoked.
/// 2. Automatically classifies domain variables using ComponentStateClassifier (climbing inheritance hierarchies).
/// 3. Registers the component with ScopedComponentStateRegistry using WeakReferences to prevent memory leaks.
/// 4. Synchronously queries Redis Cluster (IDistributedCache) and populates domain variables in RAM
///    BEFORE component lifecycle methods (OnInitialized/OnParametersSet) execute.
/// </summary>
public class ZeroTouchComponentActivator : IComponentActivator
{
    private readonly IServiceProvider _serviceProvider;
    private readonly StatePreservationOptions _options;
    private readonly ILogger<ZeroTouchComponentActivator>? _logger;

    public ZeroTouchComponentActivator(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
        _options = _serviceProvider.GetService<IOptions<StatePreservationOptions>>()?.Value ?? new StatePreservationOptions();
        _logger = _serviceProvider.GetService<ILogger<ZeroTouchComponentActivator>>();
    }

    public IComponent CreateInstance(Type componentType)
    {
        // 1. Instantiate the component using standard DI / reflection
        var instance = (IComponent)ActivatorUtilities.CreateInstance(_serviceProvider, componentType);

        if (!_options.EnableStatePreservation)
        {
            return instance;
        }

        // 2. Automatically classify domain variables across inheritance hierarchies
        var domainMembers = ComponentStateClassifier.GetDomainStateMembers(componentType);
        if (domainMembers.Length == 0)
        {
            return instance;
        }

        try
        {
            // 3. Register with Scoped Component Registry (uses WeakReference to prevent memory leaks)
            var registry = _serviceProvider.GetService<ScopedComponentStateRegistry>();
            var componentId = componentType.FullName ?? componentType.Name;
            registry?.Register(componentId, instance, domainMembers);

            // 4. LOW-LEVEL FRAMEWORK INTERCEPTION: Rehydrate RAM from Redis before initial render!
            var httpContext = _serviceProvider.GetService<IHttpContextAccessor>()?.HttpContext;
            var sessionId = httpContext?.User.FindFirst("SessionId")?.Value ?? httpContext?.Connection.Id ?? "default_session";
            var cache = _serviceProvider.GetService<IDistributedCache>();

            if (cache != null)
            {
                var redisKey = $"{_options.RedisKeyPrefix}:{sessionId}:Component:{componentId}";
                var json = cache.GetString(redisKey);

                if (!string.IsNullOrEmpty(json))
                {
                    var stateDict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json, StateSerializationConfig.Options);
                    if (stateDict != null)
                    {
                        int rehydratedCount = 0;
                        foreach (var member in domainMembers)
                        {
                            try
                            {
                                if (stateDict.TryGetValue(member.Name, out var element))
                                {
                                    var targetType = member is PropertyInfo p ? p.PropertyType : ((FieldInfo)member).FieldType;
                                    var val = JsonSerializer.Deserialize(element, targetType, StateSerializationConfig.Options);

                                    if (member is PropertyInfo prop) prop.SetValue(instance, val);
                                    else ((FieldInfo)member).SetValue(instance, val);

                                    rehydratedCount++;
                                }
                            }
                            catch (Exception ex)
                            {
                                // Isolated failure: if one property fails to deserialize due to version mismatch, skip it and continue
                                _logger?.LogWarning(ex, "Failed to rehydrate property [{Property}] on component [{Component}]. Skipping property.",
                                    member.Name, componentId);
                            }
                        }

                        if (_options.EnableDiagnostics && rehydratedCount > 0)
                        {
                            _logger?.LogDebug("Rehydrated {Count} domain variables into component [{Component}] from Redis.",
                                rehydratedCount, componentId);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            // Network resiliency: never throw an exception that would abort component creation or crash the UI page
            _logger?.LogError(ex, "Error during zero-touch state rehydration for component [{Component}]. Proceeding with default state.",
                componentType.FullName);
        }

        return instance;
    }
}
