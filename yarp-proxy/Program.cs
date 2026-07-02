using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = WebApplication.CreateBuilder(args);

// 1. Configure Forwarded Headers for Kubernetes Ingress / Load Balancers
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | 
                               ForwardedHeaders.XForwardedProto | 
                               ForwardedHeaders.XForwardedHost;
    
    // Clear known networks/proxies when running inside K8s cluster so headers from ingress are trusted
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});

// 2. Add YARP Reverse Proxy with Active Health Checks and Cookie Session Affinity
builder.Services.AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));

// Add health check service for the proxy itself
builder.Services.AddHealthChecks();

var app = builder.Build();

app.UseForwardedHeaders();

if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

// 3. Log proxy failovers and session affinity redistributions
app.Use(async (context, next) =>
{
    await next();

    // If YARP redistributed a session due to a draining pod (503 Readiness / Transport failure),
    // log the redistribution event so operators can correlate with Blazor [PersistState] rehydration logs.
    if (context.Response.Headers.TryGetValue("Set-Cookie", out var setCookie) &&
        setCookie.ToString().Contains(".Blazor.Affinity"))
    {
        var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();
        logger.LogInformation("YARP assigned or redistributed Blazor session affinity cookie for client {ClientIp} to destination {Destination}.",
            context.Connection.RemoteIpAddress,
            context.Response.Headers["X-Yarp-Destination"]);
    }
});

app.UseRouting();

// 4. Proxy endpoints (includes native WebSocket support for SignalR circuits)
app.MapReverseProxy();

// Health check endpoint for the proxy deployment in K8s
app.MapHealthChecks("/healthz");

app.Run();
