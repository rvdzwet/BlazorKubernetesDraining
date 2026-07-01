using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace BlazorKubernetesDraining;

/// <summary>
/// Scoped registry that tracks live Razor component instances within the current Blazor circuit.
/// 
/// Memory Leak Prevention & Lifecycle Resiliency:
/// Uses WeakReference&lt;object&gt; to track instantiated components. When a user navigates away from a page
/// and Blazor disposes the component, the Garbage Collector can freely reclaim the component's memory.
/// Dead weak references are automatically pruned during registry operations.
/// </summary>
public class ScopedComponentStateRegistry
{
    private readonly ConcurrentDictionary<string, (WeakReference<object> InstanceRef, MemberInfo[] Members)> _trackedComponents = new();

    /// <summary>
    /// Registers an instantiated component and its classified domain state members using a weak reference.
    /// </summary>
    public void Register(string componentId, object instance, MemberInfo[] domainMembers)
    {
        if (domainMembers.Length > 0)
        {
            _trackedComponents[componentId] = (new WeakReference<object>(instance), domainMembers);
            PruneDeadReferences();
        }
    }

    /// <summary>
    /// Returns all live component instances currently active in memory for this circuit, pruning dead weak references.
    /// </summary>
    public IEnumerable<(string ComponentId, object Instance, MemberInfo[] Members)> GetTrackedComponents()
    {
        var liveComponents = new List<(string ComponentId, object Instance, MemberInfo[] Members)>();

        foreach (var kvp in _trackedComponents.ToArray())
        {
            if (kvp.Value.InstanceRef.TryGetTarget(out var liveInstance))
            {
                liveComponents.Add((kvp.Key, liveInstance, kvp.Value.Members));
            }
            else
            {
                // Component was garbage collected after navigation; prune the dead tracking entry
                _trackedComponents.TryRemove(kvp.Key, out _);
            }
        }

        return liveComponents;
    }

    private void PruneDeadReferences()
    {
        // Prune if tracking map grows beyond threshold to ensure zero memory creep in long-lived sessions
        if (_trackedComponents.Count > 100)
        {
            foreach (var kvp in _trackedComponents.ToArray())
            {
                if (!kvp.Value.InstanceRef.TryGetTarget(out _))
                {
                    _trackedComponents.TryRemove(kvp.Key, out _);
                }
            }
        }
    }
}
