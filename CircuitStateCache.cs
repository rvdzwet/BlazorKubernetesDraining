using System.Collections.Concurrent;
using System.Collections.Generic;

namespace BlazorKubernetesDraining;

/// <summary>
/// Singleton in-memory cache that stages pre-loaded Redis state snapshots for fast,
/// non-blocking access during component activation.
///
/// Lifecycle:
/// 1. OnCircuitOpenedAsync: PersistStateCircuitHandler loads ALL [PersistState] data from Redis
///    into this cache, keyed by session ID. This is ONE async Redis call per circuit open.
/// 2. PersistStateComponentActivator.CreateInstance: Reads from this in-memory dictionary
///    instead of making synchronous Redis calls. Cost: dictionary lookup, zero network I/O.
/// 3. OnCircuitClosedAsync: PersistStateCircuitHandler removes the session entry to prevent
///    unbounded memory growth in long-running pods.
/// </summary>
public class CircuitStateCache
{
    // Outer key: session ID. Inner key: "Component:{FullTypeName}:{MemberName}" or "DI:{FullTypeName}:{MemberName}"
    private readonly ConcurrentDictionary<string, Dictionary<string, string>> _sessionSnapshots = new();

    /// <summary>
    /// Stores a pre-loaded state snapshot for a session. Called once during OnCircuitOpenedAsync.
    /// </summary>
    public void StoreSnapshot(string sessionId, Dictionary<string, string> snapshot)
    {
        _sessionSnapshots[sessionId] = snapshot;
    }

    /// <summary>
    /// Retrieves a single member value from the pre-loaded snapshot. Returns null if not found.
    /// </summary>
    public string? GetMemberValue(string sessionId, string memberKey)
    {
        if (_sessionSnapshots.TryGetValue(sessionId, out var snapshot))
        {
            snapshot.TryGetValue(memberKey, out var value);
            return value;
        }
        return null;
    }

    /// <summary>
    /// Removes the session snapshot from memory. Called during OnCircuitClosedAsync to prevent memory leaks.
    /// </summary>
    public void EvictSession(string sessionId)
    {
        _sessionSnapshots.TryRemove(sessionId, out _);
    }
}
