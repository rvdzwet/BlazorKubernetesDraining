using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BlazorKubernetesDraining;

/// <summary>
/// Custom ASP.NET Core IHostLifetime implementation that intercepts OS termination signals (SIGTERM/SIGINT)
/// at the C# application level before ASP.NET Core initiates framework shutdown.
/// 
/// Cross-Platform Compatibility:
/// - Linux / Kubernetes: Intercepts POSIX SIGTERM and SIGINT.
/// - Windows: Intercepts CTRL_C_EVENT (SIGINT), CTRL_BREAK_EVENT, and emulated SIGTERM generated during
///   Windows console closure (CTRL_CLOSE_EVENT / CTRL_SHUTDOWN_EVENT) or Windows Server Container termination.
/// 
/// Forcing an Immediate Hard Stop:
/// 1. Second Signal: Sending a second SIGTERM or pressing Ctrl+C a second time immediately aborts the drain and forces shutdown.
/// 2. Configuration: Setting EnableDraining = false or DrainTimeoutSeconds &lt;= 0 bypasses draining completely.
/// 3. OS Kill: Sending SIGKILL (kill -9 or taskkill /F) cannot be intercepted and terminates the process instantly.
/// </summary>
public class DrainingHostLifetime : IHostLifetime, IDisposable
{
    private readonly IHostApplicationLifetime _applicationLifetime;
    private readonly ActiveCircuitTracker _circuitTracker;
    private readonly CircuitDrainingState _drainingState;
    private readonly DrainingOptions _options;
    private readonly ILogger<DrainingHostLifetime> _logger;
    private readonly ILoggerFactory _loggerFactory;

    private PosixSignalRegistration? _sigTermRegistration;
    private PosixSignalRegistration? _sigIntRegistration;
    private volatile bool _signalReceived;

    // Tracks the background drain task so StopAsync can await its completion before
    // the host disposes logging providers and other infrastructure services.
    private Task? _drainTask;
    private readonly TaskCompletionSource _drainCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public DrainingHostLifetime(
        IHostApplicationLifetime applicationLifetime,
        ActiveCircuitTracker circuitTracker,
        CircuitDrainingState drainingState,
        IOptions<DrainingOptions> options,
        ILogger<DrainingHostLifetime> logger,
        ILoggerFactory loggerFactory)
    {
        _applicationLifetime = applicationLifetime;
        _circuitTracker = circuitTracker;
        _drainingState = drainingState;
        _options = options.Value;
        _logger = logger;
        _loggerFactory = loggerFactory;
    }

    public Task WaitForStartAsync(CancellationToken cancellationToken)
    {
        _sigTermRegistration = PosixSignalRegistration.Create(PosixSignal.SIGTERM, OnSignalReceived);
        _sigIntRegistration = PosixSignalRegistration.Create(PosixSignal.SIGINT, OnSignalReceived);

        _logger.LogInformation("DrainingHostLifetime registered. Ready to intercept OS termination signals (SIGTERM/SIGINT) across Windows and Linux.");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Called by the host during shutdown. Awaits the background drain task (if running)
    /// and flushes logging providers BEFORE the host disposes them.
    /// </summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // Wait for the drain task to complete (if one is running).
        // This ensures all drain-loop log entries are emitted before we flush.
        if (_drainTask != null)
        {
            // Race between drain completion and the host's ShutdownTimeout cancellation token
            await Task.WhenAny(_drainTask, Task.Delay(Timeout.Infinite, cancellationToken))
                      .ConfigureAwait(false);
        }

        // Flush all logging providers. This disposes async sinks (Serilog, Seq, NLog)
        // and forces them to write their internal buffers to disk/network BEFORE
        // the host's DI container disposes them out from under us.
        FlushLogProviders();
    }

    private void OnSignalReceived(PosixSignalContext context)
    {
        // If a second signal arrives while draining is already in progress,
        // force an immediate hard stop without waiting for circuits to drain.
        if (_signalReceived)
        {
            _logger.LogCritical("SECOND {Signal} received! Aborting graceful drain and forcing IMMEDIATE HARD SHUTDOWN!", context.Signal);
            FlushLogProviders();
            context.Cancel = false;
            _applicationLifetime.StopApplication();
            return;
        }

        _signalReceived = true;

        // Check if draining is globally disabled via configuration or zero timeout
        if (!_options.EnableDraining || _options.DrainTimeoutSeconds <= 0)
        {
            _logger.LogInformation("OS Signal [{Signal}] received. Circuit draining is disabled (EnableDraining=false or DrainTimeoutSeconds<=0). Proceeding with immediate StopApplication().", context.Signal);
            FlushLogProviders();
            _applicationLifetime.StopApplication();
            return;
        }

        // Tell the OS / .NET runtime NOT to terminate the process
        context.Cancel = true;
        _logger.LogWarning("OS Signal [{Signal}] intercepted by DrainingHostLifetime! Preventing default StopApplication()...", context.Signal);

        // Mark pod as shutting down so Readiness probe returns 503 Unhealthy
        _drainingState.IsShuttingDown = true;

        int initialCircuits = _circuitTracker.ActiveCircuits;
        if (initialCircuits == 0)
        {
            _logger.LogInformation("No active Blazor circuits detected upon {Signal}. Triggering immediate StopApplication().", context.Signal);
            FlushLogProviders();
            _applicationLifetime.StopApplication();
            return;
        }

        _logger.LogWarning("Intercepted {Signal} with {Count} active Blazor circuits! Holding off ASP.NET Core shutdown for up to {Timeout}s...",
            context.Signal, initialCircuits, _options.DrainTimeoutSeconds);

        // Start background draining task. Store the reference so StopAsync can await it.
        _drainTask = Task.Run(async () => await DrainCircuitsAndShutDownAsync());
    }

    private async Task DrainCircuitsAndShutDownAsync()
    {
        try
        {
            var stopwatch = Stopwatch.StartNew();
            var maxDuration = TimeSpan.FromSeconds(_options.DrainTimeoutSeconds);
            var pollInterval = TimeSpan.FromMilliseconds(_options.PollingIntervalMilliseconds);

            int lastReportedCount = _circuitTracker.ActiveCircuits;
            var lastLogTime = stopwatch.Elapsed;

            while (stopwatch.Elapsed < maxDuration)
            {
                int currentCircuits = _circuitTracker.ActiveCircuits;

                if (currentCircuits == 0)
                {
                    _logger.LogInformation("All Blazor circuits drained out successfully after {Elapsed:F1}s! Now calling StopApplication()...",
                        stopwatch.Elapsed.TotalSeconds);
                    FlushLogProviders();
                    _applicationLifetime.StopApplication();
                    return;
                }

                if (currentCircuits != lastReportedCount || (_options.EnableVerboseLogging && (stopwatch.Elapsed - lastLogTime).TotalSeconds >= 15))
                {
                    _logger.LogInformation("Code-level draining in progress... Remaining active circuits: {Count}. Time elapsed: {Elapsed:F1}s / {Max}s.",
                        currentCircuits, stopwatch.Elapsed.TotalSeconds, _options.DrainTimeoutSeconds);
                    lastReportedCount = currentCircuits;
                    lastLogTime = stopwatch.Elapsed;
                }

                try
                {
                    await Task.Delay(pollInterval);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error during circuit draining delay loop.");
                    break;
                }
            }

            int remaining = _circuitTracker.ActiveCircuits;
            _logger.LogWarning("Drain timeout ({Timeout}s) expired! Triggering StopApplication() with {Remaining} circuits still open.",
                _options.DrainTimeoutSeconds, remaining);

            FlushLogProviders();
            _applicationLifetime.StopApplication();
        }
        finally
        {
            _drainCompleted.TrySetResult();
        }
    }

    /// <summary>
    /// Forces all logging providers to flush their internal async buffers to disk/network.
    /// Must be called BEFORE StopApplication() to ensure log entries are not lost when
    /// the host disposes logging infrastructure during shutdown.
    /// 
    /// Works generically across all ASP.NET Core logging providers:
    /// - Serilog: Disposes async sinks and flushes batched HTTP/file writers.
    /// - NLog: Flushes async target wrappers.
    /// - Seq: Flushes batched HTTP sink.
    /// - Console: Flushes stdout/stderr buffers.
    /// </summary>
    private void FlushLogProviders()
    {
        try
        {
            // ILoggerFactory.Dispose() calls Dispose() on all registered ILoggerProviders,
            // which forces async providers to flush their internal buffers synchronously.
            // This is safe to call multiple times — disposed providers become no-ops.
            (_loggerFactory as IDisposable)?.Dispose();
        }
        catch (Exception ex)
        {
            // Last-resort: write directly to stderr if logging infrastructure is already dead
            Console.Error.WriteLine($"[DrainingHostLifetime] Error flushing log providers: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _sigTermRegistration?.Dispose();
        _sigIntRegistration?.Dispose();
        GC.SuppressFinalize(this);
    }
}
