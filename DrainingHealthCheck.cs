using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace BlazorKubernetesDraining;

/// <summary>
/// Health check implementation for Kubernetes Readiness Probes.
/// Returns Healthy during normal operations, but immediately returns Unhealthy (503)
/// once application shutdown (SIGTERM) is initiated, preventing Kubernetes from sending new traffic to this pod.
/// </summary>
public class DrainingReadinessHealthCheck : IHealthCheck
{
    private readonly CircuitDrainingState _drainingState;

    public DrainingReadinessHealthCheck(CircuitDrainingState drainingState)
    {
        _drainingState = drainingState;
    }

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        if (_drainingState.IsShuttingDown)
        {
            // Return Unhealthy so Kubernetes removes this pod from Service/Ingress endpoints immediately.
            return Task.FromResult(HealthCheckResult.Unhealthy(
                "Pod is terminating and draining open Blazor circuits. Not accepting new connections."));
        }

        return Task.FromResult(HealthCheckResult.Healthy("Pod is ready and accepting connections."));
    }
}

/// <summary>
/// Health check implementation for Kubernetes Liveness Probes.
/// Remains Healthy (200 OK) even during the shutdown/draining window so Kubernetes does not prematurely
/// send SIGKILL while active Blazor circuits are finishing their work.
/// </summary>
public class DrainingLivenessHealthCheck : IHealthCheck
{
    private readonly CircuitDrainingState _drainingState;

    public DrainingLivenessHealthCheck(CircuitDrainingState drainingState)
    {
        _drainingState = drainingState;
    }

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        // Notice: Even if _drainingState.IsShuttingDown is true, Liveness must return Healthy
        // as long as the process is responsive and draining hasn't exceeded the grace period.
        return Task.FromResult(HealthCheckResult.Healthy("Pod process is alive."));
    }
}

/// <summary>
/// Shared singleton state holding the shutdown status across health checks and hosted services.
/// </summary>
public class CircuitDrainingState
{
    private volatile bool _isShuttingDown;

    public bool IsShuttingDown
    {
        get => _isShuttingDown;
        set => _isShuttingDown = value;
    }
}
