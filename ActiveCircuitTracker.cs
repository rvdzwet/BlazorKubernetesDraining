using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.Extensions.Logging;

namespace BlazorKubernetesDraining;

/// <summary>
/// Tracks active Blazor Server circuits and connections in real-time.
/// Registered as a scoped or singleton CircuitHandler in the DI container.
/// </summary>
public class ActiveCircuitTracker : CircuitHandler
{
    private int _activeCircuits;
    private int _activeConnections;
    private readonly ILogger<ActiveCircuitTracker> _logger;

    public ActiveCircuitTracker(ILogger<ActiveCircuitTracker> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Gets the current number of open Blazor circuits (stateful sessions in memory).
    /// </summary>
    public int ActiveCircuits => Volatile.Read(ref _activeCircuits);

    /// <summary>
    /// Gets the current number of active SignalR web socket / transport connections.
    /// </summary>
    public int ActiveConnections => Volatile.Read(ref _activeConnections);

    /// <summary>
    /// Returns true if there are any open Blazor circuits still running on this pod.
    /// </summary>
    public bool HasActiveCircuits => ActiveCircuits > 0;

    public override Task OnCircuitOpenedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        var count = Interlocked.Increment(ref _activeCircuits);
        _logger.LogDebug("Blazor circuit opened [{CircuitId}]. Total active circuits: {Count}", circuit.Id, count);
        return base.OnCircuitOpenedAsync(circuit, cancellationToken);
    }

    public override Task OnCircuitClosedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        var count = Interlocked.Decrement(ref _activeCircuits);
        _logger.LogDebug("Blazor circuit closed [{CircuitId}]. Total active circuits: {Count}", circuit.Id, count);
        return base.OnCircuitClosedAsync(circuit, cancellationToken);
    }

    public override Task OnConnectionUpAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        var count = Interlocked.Increment(ref _activeConnections);
        _logger.LogDebug("Blazor connection UP [{CircuitId}]. Total active connections: {Count}", circuit.Id, count);
        return base.OnConnectionUpAsync(circuit, cancellationToken);
    }

    public override Task OnConnectionDownAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        var count = Interlocked.Decrement(ref _activeConnections);
        _logger.LogDebug("Blazor connection DOWN [{CircuitId}]. Total active connections: {Count}", circuit.Id, count);
        return base.OnConnectionDownAsync(circuit, cancellationToken);
    }
}
