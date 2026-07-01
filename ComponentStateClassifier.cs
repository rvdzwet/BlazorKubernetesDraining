using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;

namespace BlazorKubernetesDraining;

/// <summary>
/// High-performance metadata classifier that discovers members decorated with [PersistState]
/// on Razor components and Scoped DI services.
///
/// Design Decisions:
/// - Only members explicitly annotated with [PersistState] are included (opt-in, not opt-out).
/// - Climbs the full inheritance hierarchy up to ComponentBase / object to capture base class state.
/// - Results are cached per Type in a thread-safe ConcurrentDictionary for zero repeated reflection cost.
/// </summary>
public static class ComponentStateClassifier
{
    private static readonly ConcurrentDictionary<Type, MemberInfo[]> _cache = new();

    /// <summary>
    /// Returns all [PersistState]-annotated members for the given type. Returns an empty array
    /// (cached) for types with no annotated members, enabling fast early-exit in callers.
    /// </summary>
    public static MemberInfo[] GetPersistedMembers(Type targetType)
    {
        return _cache.GetOrAdd(targetType, static type =>
        {
            var members = new List<MemberInfo>();
            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;

            // Climb the inheritance hierarchy to capture [PersistState] on intermediate base classes
            var currentType = type;
            while (currentType != null && currentType != typeof(Microsoft.AspNetCore.Components.ComponentBase) && currentType != typeof(object))
            {
                foreach (var prop in currentType.GetProperties(flags))
                {
                    if (prop.GetCustomAttribute<PersistStateAttribute>() != null && prop.CanRead && prop.CanWrite)
                    {
                        members.Add(prop);
                    }
                }

                foreach (var field in currentType.GetFields(flags))
                {
                    if (field.GetCustomAttribute<PersistStateAttribute>() != null && !field.IsInitOnly)
                    {
                        members.Add(field);
                    }
                }

                currentType = currentType.BaseType;
            }

            return members.ToArray();
        });
    }
}
