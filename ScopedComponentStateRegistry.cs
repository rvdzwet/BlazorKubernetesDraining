using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace BlazorKubernetesDraining;

/// <summary>
/// Scoped registry that tracks live Razor component instances within the current Blazor circuit.
/// Used by ZeroTouchComponentActivator and UniversalZeroTouchCircuitHandler to checkpoint component state to Redis.
/// </summary>
public class ScopedComponentStateRegistry
{
    private readonly ConcurrentDictionary<string, (object Instance, MemberInfo[] Members)> _trackedComponents = new();

    /// <summary>
    /// Registers an instantiated component and its classified domain state members.
    /// </summary>
    public void Register(string componentId, object instance, MemberInfo[] domainMembers)
    {
        if (domainMembers.Length > 0)
        {
            _trackedComponents[componentId] = (instance, domainMembers);
        }
    }

    /// <summary>
    /// Returns all tracked components currently active in memory for this circuit.
    /// </summary>
    public IEnumerable<(string ComponentId, object Instance, MemberInfo[] Members)> GetTrackedComponents()
    {
        return _trackedComponents.Select(kvp => (kvp.Key, kvp.Value.Instance, kvp.Value.Members));
    }
}
