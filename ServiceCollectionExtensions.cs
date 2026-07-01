using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace BlazorKubernetesDraining;

/// <summary>
/// Extension methods for registering Blazor Server C#-level SIGTERM interception, health probes,
/// and Universal Zero-Touch State Preservation in Dependency Injection.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds active circuit tracking, custom C#-level SIGTERM interception (IHostLifetime), and Kubernetes Liveness/Readiness health checks.
    /// Notice: No special Kubernetes preStop lifecycle endpoints or shell scripts are required because DrainingHostLifetime
    /// catches OS signals directly inside the application process before ASP.NET Core triggers ApplicationStopping.
    /// </summary>
    /// <param name="services">The IServiceCollection.</param>
    /// <param name="configureOptions">Optional configuration for DrainingOptions (e.g. DrainTimeoutSeconds).</param>
    /// <returns>The updated IServiceCollection.</returns>
    public static IServiceCollection AddBlazorKubernetesDraining(
        this IServiceCollection services,
        Action<DrainingOptions>? configureOptions = null)
    {
        var options = new DrainingOptions();
        configureOptions?.Invoke(options);

        services.Configure<DrainingOptions>(opt =>
        {
            opt.EnableDraining = options.EnableDraining;
            opt.DrainTimeoutSeconds = options.DrainTimeoutSeconds;
            opt.PollingIntervalMilliseconds = options.PollingIntervalMilliseconds;
            opt.EnableVerboseLogging = options.EnableVerboseLogging;
        });

        // Ensure ASP.NET Core HostOptions.ShutdownTimeout has a small safety buffer for final process exit
        services.Configure<Microsoft.Extensions.Hosting.HostOptions>(hostOpt =>
        {
            var requiredTimeout = TimeSpan.FromSeconds(15);
            if (hostOpt.ShutdownTimeout < requiredTimeout)
            {
                hostOpt.ShutdownTimeout = requiredTimeout;
            }
        });

        // Register shared state and singleton circuit tracker
        services.AddSingleton<CircuitDrainingState>();
        services.AddSingleton<ActiveCircuitTracker>();

        // Blazor resolves CircuitHandler per circuit scope; we forward to our singleton tracker
        services.AddScoped<CircuitHandler>(sp => sp.GetRequiredService<ActiveCircuitTracker>());

        // CRITICAL ARCHITECTURAL DECISION:
        // Replace ASP.NET Core's default ConsoleLifetime with our DrainingHostLifetime.
        // This allows our C# code to intercept OS SIGTERM signals directly, prevent default application teardown,
        // drain active SignalR WebSockets, and only call StopApplication() once all circuits drop to 0.
        services.AddSingleton<IHostLifetime, DrainingHostLifetime>();

        // Register Kubernetes Liveness and Readiness health checks
        services.AddHealthChecks()
            .AddCheck<DrainingReadinessHealthCheck>("readiness", tags: new[] { "ready" })
            .AddCheck<DrainingLivenessHealthCheck>("liveness", tags: new[] { "live" });

        return services;
    }

    /// <summary>
    /// Registers the Universal Zero-Touch State Preservation engine in Dependency Injection.
    /// 
    /// This enables automatic, attribute-free, interface-free domain state synchronization between ASP.NET Core RAM
    /// and Redis Cluster across Kubernetes pod failovers for BOTH:
    /// 1. Instantiated Razor Components (by replacing IComponentActivator).
    /// 2. Scoped Dependency Injected domain services (registered via AddScopedState).
    /// </summary>
    /// <param name="services">The IServiceCollection.</param>
    /// <param name="configureOptions">Optional configuration for StatePreservationOptions.</param>
    /// <returns>The updated IServiceCollection.</returns>
    public static IServiceCollection AddUniversalZeroTouchStatePreservation(
        this IServiceCollection services,
        Action<StatePreservationOptions>? configureOptions = null)
    {
        var options = new StatePreservationOptions();
        configureOptions?.Invoke(options);

        services.Configure<StatePreservationOptions>(opt =>
        {
            opt.EnableStatePreservation = options.EnableStatePreservation;
            opt.RedisKeyPrefix = options.RedisKeyPrefix;
            opt.SlidingExpiration = options.SlidingExpiration;
            opt.EnableDiagnostics = options.EnableDiagnostics;
        });

        // 1. Register Scoped Component Registry (tracks active components per user circuit)
        services.AddScoped<ScopedComponentStateRegistry>();

        // 2. Replace ASP.NET Core's default component activator with our zero-touch rehydrating activator
        services.AddSingleton<IComponentActivator, ZeroTouchComponentActivator>();

        // 3. Register our Universal CircuitHandler to orchestrate rehydration and checkpointing on failovers
        services.AddScoped<CircuitHandler, UniversalZeroTouchCircuitHandler>();

        return services;
    }

    /// <summary>
    /// Maps endpoint routes for Kubernetes readiness (/health/ready) and liveness (/health/live) probes.
    /// </summary>
    /// <param name="endpoints">The IEndpointRouteBuilder.</param>
    /// <returns>The updated IEndpointRouteBuilder.</returns>
    public static IEndpointRouteBuilder MapBlazorKubernetesDrainingHealthChecks(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = check => check.Tags.Contains("ready")
        });

        endpoints.MapHealthChecks("/health/live", new HealthCheckOptions
        {
            Predicate = check => check.Tags.Contains("live")
        });

        return endpoints;
    }
}
