using System;

namespace BlazorKubernetesDraining;

/// <summary>
/// Configuration options for Universal Zero-Touch State Preservation in Blazor Server.
/// </summary>
public class StatePreservationOptions
{
    /// <summary>
    /// Master switch to enable or disable automatic state preservation to Redis Cluster.
    /// Default is true.
    /// </summary>
    public bool EnableStatePreservation { get; set; } = true;

    /// <summary>
    /// Prefix used for all state keys stored in IDistributedCache / Redis Cluster.
    /// Default is "BlazorStore".
    /// </summary>
    public string RedisKeyPrefix { get; set; } = "BlazorStore";

    /// <summary>
    /// The sliding expiration time for saved user session state snapshots in Redis.
    /// Default is 2 hours.
    /// </summary>
    public TimeSpan SlidingExpiration { get; set; } = TimeSpan.FromHours(2);

    /// <summary>
    /// Whether to log diagnostic messages during state classification, saving, and rehydration.
    /// Default is true.
    /// </summary>
    public bool EnableDiagnostics { get; set; } = true;
}
