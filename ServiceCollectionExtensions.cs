using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace BlazorKubernetesDraining;

/// <summary>
/// Extension methods for registering Blazor Server C#-level SIGTERM interception, health probes,
/// and [PersistState] attribute-based state preservation in Dependency Injection.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds active circuit tracking, C#-level SIGTERM interception (IHostLifetime), and Kubernetes Liveness/Readiness health checks.
    /// </summary>
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

        services.Configure<Microsoft.Extensions.Hosting.HostOptions>(hostOpt =>
        {
            var requiredTimeout = TimeSpan.FromSeconds(15);
            if (hostOpt.ShutdownTimeout < requiredTimeout)
            {
                hostOpt.ShutdownTimeout = requiredTimeout;
            }
        });

        services.AddSingleton<CircuitDrainingState>();
        services.AddSingleton<ActiveCircuitTracker>();
        services.AddScoped<CircuitHandler>(sp => sp.GetRequiredService<ActiveCircuitTracker>());
        services.AddSingleton<IHostLifetime, DrainingHostLifetime>();

        services.AddHealthChecks()
            .AddCheck<DrainingReadinessHealthCheck>("readiness", tags: new[] { "ready" })
            .AddCheck<DrainingLivenessHealthCheck>("liveness", tags: new[] { "live" });

        return services;
    }

    /// <summary>
    /// Registers the [PersistState] attribute-based state preservation engine.
    ///
    /// Architecture:
    /// - IComponentActivator: Replaced with PersistStateComponentActivator. Rehydrates [PersistState]
    ///   members from an in-memory cache (no Redis calls during rendering).
    /// - CircuitHandler: PersistStateCircuitHandler performs ONE batched Redis read on circuit open
    ///   and ONE batched Redis write on connection down.
    /// - CircuitStateCache: Singleton in-memory staging area. Populated once per circuit, evicted on close.
    /// - SessionIdentityProvider: Scoped service resolving stable user identity for Redis key generation.
    ///
    /// Prerequisites:
    /// - IDistributedCache must be registered (e.g., AddStackExchangeRedisCache).
    /// - IHttpContextAccessor must be registered (AddHttpContextAccessor).
    /// - Authentication must populate User.Identity.Name or a "SessionId" claim.
    /// </summary>
    public static IServiceCollection AddPersistStatePreservation(
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

        // Singleton: in-memory staging cache for pre-loaded Redis state
        services.AddSingleton<CircuitStateCache>();

        // Scoped: session identity, component registry, circuit handler
        services.AddHttpContextAccessor();
        services.AddScoped<SessionIdentityProvider>();
        services.AddScoped<ScopedComponentStateRegistry>();
        services.AddScoped<CircuitHandler, PersistStateCircuitHandler>();

        // Singleton: replace default component activator
        services.AddSingleton<IComponentActivator, PersistStateComponentActivator>();

        return services;
    }

    /// <summary>
    /// Maps endpoint routes for Kubernetes readiness (/health/ready) and liveness (/health/live) probes.
    /// </summary>
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
