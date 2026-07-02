using System;
using System.Globalization;
using Microsoft.Extensions.Logging;

namespace BlazorKubernetesDraining;

/// <summary>
/// Scoped service (one per Blazor circuit) that notifies front-end components when Kubernetes pod draining
/// (SIGTERM) has been initiated, while preserving the circuit's exact localization culture.
/// 
/// Why Culture Preservation is Required:
/// When OS termination signals (SIGTERM) arrive, background thread pool threads raise shutdown events
/// with the default system culture (e.g., InvariantCulture). If components call InvokeAsync(StateHasChanged)
/// directly from those events, ExecutionContext flows the invariant culture into Blazor's synchronization context,
/// causing IStringLocalizer to fail resource lookup and revert translated strings to their technical keys.
/// 
/// How This Works:
/// 1. When injected into a circuit, CircuitDrainingNotifier captures the user's CurrentCulture and CurrentUICulture.
/// 2. It subscribes to the singleton CircuitDrainingState shutdown event.
/// 3. When SIGTERM arrives, before raising OnDrainingStarted, it temporarily sets the thread's CultureInfo so that
///    any InvokeAsync(StateHasChanged) call captures the correct culture in its ExecutionContext and flows it to the UI.
/// 4. Implements IDisposable to cleanly unsubscribe when the circuit closes, preventing memory leaks.
/// </summary>
public interface ICircuitDrainingNotifier
{
    /// <summary>
    /// Gets whether Kubernetes pod draining has been initiated for this pod.
    /// </summary>
    bool IsShuttingDown { get; }

    /// <summary>
    /// Event raised when SIGTERM is received and circuit draining begins.
    /// Subscribing components can safely call InvokeAsync(StateHasChanged) without losing localized strings.
    /// </summary>
    event Action? OnDrainingStarted;
}

public class CircuitDrainingNotifier : ICircuitDrainingNotifier, IDisposable
{
    private readonly CircuitDrainingState _drainingState;
    private readonly ILogger<CircuitDrainingNotifier>? _logger;
    private readonly CultureInfo _circuitCulture;
    private readonly CultureInfo _circuitUICulture;
    private bool _disposed;

    public CircuitDrainingNotifier(
        CircuitDrainingState drainingState,
        ILogger<CircuitDrainingNotifier>? logger = null)
    {
        _drainingState = drainingState;
        _logger = logger;

        // Capture the circuit's active localization culture at the moment this scoped service is instantiated
        _circuitCulture = CultureInfo.CurrentCulture;
        _circuitUICulture = CultureInfo.CurrentUICulture;

        // Subscribe to the singleton shutdown notification
        _drainingState.OnShutdownInitiated += HandleShutdownInitiated;
    }

    public bool IsShuttingDown => _drainingState.IsShuttingDown;

    public event Action? OnDrainingStarted;

    private void HandleShutdownInitiated()
    {
        if (_disposed)
        {
            return;
        }

        var previousCulture = CultureInfo.CurrentCulture;
        var previousUICulture = CultureInfo.CurrentUICulture;

        try
        {
            // Set the thread culture to the circuit's captured culture.
            // When the component's event handler calls InvokeAsync(StateHasChanged), AsyncLocal ExecutionContext
            // will capture this exact culture and flow it into Blazor's UI rendering synchronization context!
            CultureInfo.CurrentCulture = _circuitCulture;
            CultureInfo.CurrentUICulture = _circuitUICulture;

            _logger?.LogDebug("Dispatching OnDrainingStarted for circuit with culture [{Culture}] / [{UICulture}].",
                _circuitCulture.Name, _circuitUICulture.Name);

            OnDrainingStarted?.Invoke();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error dispatching OnDrainingStarted to circuit components.");
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
            CultureInfo.CurrentUICulture = previousUICulture;
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _drainingState.OnShutdownInitiated -= HandleShutdownInitiated;
            GC.SuppressFinalize(this);
        }
    }
}
