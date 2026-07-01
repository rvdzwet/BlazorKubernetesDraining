using System;
using Microsoft.Extensions.DependencyInjection;

namespace BlazorKubernetesDraining;

/// <summary>
/// Extension methods for registering stateful Scoped Dependency Injected services (e.g., View Models, Shopping Cart services)
/// so their in-memory domain state is automatically captured and rehydrated across Kubernetes pod failovers.
/// </summary>
public static class ScopedStateExtensions
{
    /// <summary>
    /// Registers a Scoped service in Dependency Injection and marks it for automatic zero-touch state preservation.
    /// When a pod fails over, any public or private domain state inside this service is rehydrated from Redis Cluster
    /// before components are rendered.
    /// </summary>
    public static IServiceCollection AddScopedState<TService>(this IServiceCollection services) where TService : class
    {
        return services.AddScopedState<TService, TService>();
    }

    /// <summary>
    /// Registers a Scoped service with an interface in Dependency Injection and marks it for automatic zero-touch state preservation.
    /// </summary>
    public static IServiceCollection AddScopedState<TService, TImplementation>(this IServiceCollection services)
        where TService : class
        where TImplementation : class, TService
    {
        // 1. Register the service normally as Scoped
        services.AddScoped<TService, TImplementation>();

        // 2. Register with our Scoped State DI Registry for automatic circuit interception
        services.AddScoped<IScopedStateServiceRegistration>(sp => new ScopedStateServiceRegistration(
            typeof(TService),
            sp.GetRequiredService<TService>()!));

        return services;
    }
}

/// <summary>
/// Represents a registered Scoped DI service that holds in-memory domain state.
/// </summary>
public interface IScopedStateServiceRegistration
{
    Type ServiceType { get; }
    object Instance { get; }
}

public record ScopedStateServiceRegistration(Type ServiceType, object Instance) : IScopedStateServiceRegistration;
