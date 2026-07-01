namespace BlazorKubernetesDraining;

/// <summary>
/// Configuration options for Blazor Server circuit draining during OS termination signals (SIGTERM/SIGINT).
/// </summary>
public class DrainingOptions
{
    /// <summary>
    /// Master switch to enable or disable circuit draining. If set to false, receiving SIGTERM/SIGINT
    /// will immediately trigger ASP.NET Core shutdown without waiting for active circuits to close.
    /// Default is true.
    /// </summary>
    public bool EnableDraining { get; set; } = true;

    /// <summary>
    /// The maximum duration, in seconds, to wait for open Blazor SignalR circuits to drain out 
    /// after receiving SIGTERM/SIGINT. If set to <= 0, draining is bypassed. Default is 600 seconds (10 minutes).
    /// </summary>
    public int DrainTimeoutSeconds { get; set; } = 600;

    /// <summary>
    /// The polling interval, in milliseconds, at which the draining service checks if active circuits have dropped to zero.
    /// Default is 1000 milliseconds (1 second).
    /// </summary>
    public int PollingIntervalMilliseconds { get; set; } = 1000;

    /// <summary>
    /// Whether to log detailed diagnostic messages during the draining loop.
    /// </summary>
    public bool EnableVerboseLogging { get; set; } = true;
}
