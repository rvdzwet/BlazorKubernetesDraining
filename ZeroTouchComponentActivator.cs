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
/// 2. It automatically classifies all private and public domain variables using ComponentStateClassifier.
/// 3. Registers the component instance with ScopedComponentStateRegistry for automatic background checkpointing.
/// 4. Synchronously queries Redis Cluster (IDistributedCache) and populates the component's domain variables in RAM
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

        // 2. Automatically classify domain variables without requiring attributes or interfaces
        var domainMembers = ComponentStateClassifier.GetDomainStateMembers(componentType);
        if (domainMembers.Length == 0)
        {
            return instance;
        }

        try
        {
            // 3. Register with Scoped Component Registry for checkpointing
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
                    var stateDict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
                    if (stateDict != null)
                    {
                        foreach (var member in domainMembers)
                        {
                            if (stateDict.TryGetValue(member.Name, out var element))
                            {
                                var targetType = member is PropertyInfo p ? p.PropertyType : ((FieldInfo)member).FieldType;
                                var val = JsonSerializer.Deserialize(element, targetType);

                                if (member is PropertyInfo prop) prop.SetValue(instance, val);
                                else ((FieldInfo)member).SetValue(instance, val);
                            }
                        }

                        if (_options.EnableDiagnostics)
                        {
                            _logger?.LogDebug("Rehydrated {Count} domain state variables into component [{Component}] from Redis.",
                                stateDict.Count, componentId);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error during zero-touch state rehydration for component [{Component}].", componentType.FullName);
        }

        return instance;
    }
}
