using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;

namespace BlazorKubernetesDraining;

/// <summary>
/// High-performance automated metadata classifier that inspects any C# Type (Razor Component or Scoped DI Service)
/// and discovers all instance domain variables without requiring attributes, interfaces, or manual annotations.
/// 
/// Enterprise Resiliency & Failure Mode Protections:
/// 1. Hierarchy Climbing: Scans across custom component base classes while cleanly stopping at ComponentBase.
/// 2. Non-Serializable Exclusion: Automatically filters out runtime primitives (Streams, Tasks, Timers, CancellationTokens,
///    Reflection metadata, and sockets) that would cause JSON serialization exceptions or hangs.
/// 3. Injected Service Filtering: Ignores DI services marked with [Inject] or framework primitives (RenderHandle, EventCallback).
/// 4. Zero-Overhead Caching: Results are cached per Type in a thread-safe ConcurrentDictionary.
/// </summary>
public static class ComponentStateClassifier
{
    private static readonly ConcurrentDictionary<Type, MemberInfo[]> _stateMembersCache = new();

    /// <summary>
    /// Gets all discoverable domain state members (fields and properties) for the given type across its inheritance hierarchy.
    /// </summary>
    public static MemberInfo[] GetDomainStateMembers(Type targetType)
    {
        return _stateMembersCache.GetOrAdd(targetType, type =>
        {
            var members = new List<MemberInfo>();
            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;

            // Climb the inheritance hierarchy to capture domain variables declared on intermediate base classes
            var currentType = type;
            while (currentType != null && currentType != typeof(ComponentBase) && currentType != typeof(object))
            {
                // 1. Discover Properties
                foreach (var prop in currentType.GetProperties(flags))
                {
                    if (IsDomainState(prop.PropertyType, prop.GetCustomAttribute<InjectAttribute>() != null, prop.CanRead && prop.CanWrite))
                    {
                        members.Add(prop);
                    }
                }

                // 2. Discover Fields (including private backing fields for automatic properties if needed)
                foreach (var field in currentType.GetFields(flags))
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

                currentType = currentType.BaseType;
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

        // Filter out non-serializable runtime primitives, threading primitives, streams, and IO/network sockets
        if (typeof(Stream).IsAssignableFrom(memberType) ||
            typeof(Task).IsAssignableFrom(memberType) ||
            typeof(CancellationTokenSource).IsAssignableFrom(memberType) ||
            typeof(CancellationToken).IsAssignableFrom(memberType) ||
            typeof(Timer).IsAssignableFrom(memberType) ||
            typeof(MemberInfo).IsAssignableFrom(memberType))
        {
            return false;
        }

        var ns = memberType.Namespace ?? string.Empty;
        if (ns.StartsWith("System.Threading") || ns.StartsWith("System.IO") ||
            ns.StartsWith("System.Net") || ns.StartsWith("System.Reflection"))
        {
            return false;
        }

        return true;
    }
}
