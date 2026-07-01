using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using Microsoft.AspNetCore.Components;

namespace BlazorKubernetesDraining;

/// <summary>
/// High-performance automated metadata classifier that inspects any C# Type (Razor Component or Scoped DI Service)
/// and discovers all instance domain variables without requiring attributes, interfaces, or manual annotations.
/// 
/// How it works:
/// 1. Scans all private and public instance fields and properties declared on the type.
/// 2. Filters out Blazor framework primitives (RenderHandle, EventCallback, ElementReference, RenderFragment).
/// 3. Filters out Dependency Injection services marked with [Inject] or inheriting from IComponent.
/// 4. Caches the resulting domain members per Type for zero-overhead runtime serialization and rehydration.
/// </summary>
public static class ComponentStateClassifier
{
    private static readonly ConcurrentDictionary<Type, MemberInfo[]> _stateMembersCache = new();

    /// <summary>
    /// Gets all discoverable domain state members (fields and properties) for the given type.
    /// </summary>
    public static MemberInfo[] GetDomainStateMembers(Type targetType)
    {
        return _stateMembersCache.GetOrAdd(targetType, type =>
        {
            var members = new List<MemberInfo>();
            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;

            // 1. Discover Properties
            foreach (var prop in type.GetProperties(flags))
            {
                if (IsDomainState(prop.PropertyType, prop.GetCustomAttribute<InjectAttribute>() != null, prop.CanRead && prop.CanWrite))
                {
                    members.Add(prop);
                }
            }

            // 2. Discover Fields (including private backing fields for automatic properties if needed)
            foreach (var field in type.GetFields(flags))
            {
                // Skip compiler-generated property backing fields if the property itself was already captured
                if (field.Name.EndsWith("k__BackingField"))
                {
                    continue;
                }

                if (IsDomainState(field.FieldType, field.GetCustomAttribute<InjectAttribute>() != null, !field.IsInitOnly))
                {
                    members.Add(field);
                }
            }

            return members.ToArray();
        });
    }

    private static bool IsDomainState(Type memberType, bool isInjected, bool isWritable)
    {
        if (!isWritable || isInjected)
        {
            return false;
        }

        // Filter out Blazor framework primitives, UI delegates, and UI render references
        if (typeof(IComponent).IsAssignableFrom(memberType) || typeof(Delegate).IsAssignableFrom(memberType))
        {
            return false;
        }

        if (memberType == typeof(RenderHandle) || memberType == typeof(RenderFragment) ||
            memberType == typeof(ElementReference) || memberType.Name.StartsWith("EventCallback"))
        {
            return false;
        }

        // Ignore standard System/Threading synchronization primitives or tasks
        if (memberType.Namespace?.StartsWith("System.Threading") == true || memberType.Namespace?.StartsWith("System.Threading.Tasks") == true)
        {
            return false;
        }

        return true;
    }
}
