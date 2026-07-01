# Blazor Server Stateful Connection Draining & Universal Zero-Touch State Preservation Architecture

This repository contains a standalone, production-ready reference library and Kubernetes deployment configuration designed to solve the two biggest operational challenges in hosting ASP.NET Core Blazor Server applications:

1. **Stateful SignalR Connection Drops & Kerberos Authentication Teardown** during rolling updates and pod restarts.
2. **In-Memory Circuit & DI Service State Loss** across distributed pod failovers.

---

## 🏛️ Part 1: C#-Level `SIGTERM` Interception & Kerberos Sidecar Architecture

While Kubernetes-level lifecycle hooks (`preStop`) are commonly used in infrastructure-driven setups, our **Enterprise Architect** identified a critical architectural principle: **framework-specific connection draining should be encapsulated within the application layer, not leaked into platform deployment manifests.**

By replacing ASP.NET Core's default `IHostLifetime` with a custom **C#-level signal interceptor ([DrainingHostLifetime.cs](file:///c:/Users/roman/source/repos/SocketConnectionTest/DrainingHostLifetime.cs))**, we achieve:
* **100% Standard Kubernetes Manifests**: No custom `preStop` HTTP endpoints or shell script workarounds in your deployment YAML.
* **True Cross-Platform Portability**: Works identically across Linux Kubernetes pods, Windows Server IIS, Windows Containers, and Docker Swarm.
* **Flawless SignalR Preservation**: Intercepts `SIGTERM` before ASP.NET Core sees it, marks Readiness as Unhealthy (`503`), delays framework shutdown, and allows active browser circuits and native Kerberos database sidecars (`initContainers` with `restartPolicy: Always`) to finish their SQL transactions without dropping tickets.

### 🖥️ Cross-Platform Signal Behavior: Windows vs. Linux
* **Linux & Kubernetes**: Intercepts POSIX **`SIGTERM`** (signal 15) and **`SIGINT`**.
* **Windows & Windows Server Containers**: .NET 6+ automatically translates Win32 console events (`CTRL_CLOSE_EVENT`, `CTRL_SHUTDOWN_EVENT`, `CTRL_C_EVENT`) from the Windows Host Compute Service (HCS) into `PosixSignal.SIGTERM` / `SIGINT`. Draining works identically!

### ⚡ How to Force an Immediate HARD STOP Without Draining
1. **Send a Second Signal (Double `Ctrl+C` / Consecutive `SIGTERM`)**: If a signal arrives a second time while draining is in progress, `DrainingHostLifetime` aborts the drain and forces an instant shutdown.
2. **OS Hard Kill (`SIGKILL` / `taskkill /F`)**: Cannot be intercepted and terminates the process instantly in $0\text{ ms}$.
3. **Configuration Override**: Set `DrainingOptions__EnableDraining=false` or `DrainTimeoutSeconds=0` in environment variables.

---

## 🚀 Part 2: Universal Zero-Touch State Preservation Engine (Components + DI Services)

When running Blazor Server across a Kubernetes cluster with an external SignalR transport (like **Azure SignalR Service**), pod restarts do not sever browser WebSocket connections. However, when a pod dies and traffic routes to a new pod, how do you prevent Blazor from throwing *"Could not find circuit"* and wiping out user data?

This library includes a **100% invisible, zero-touch state preservation engine** that automatically synchronizes in-memory domain state between ASP.NET Core RAM and Redis Cluster across pod failovers—**without requiring developers to write caching code, implement attributes, or inherit from artificial interfaces!**

### 🧠 The Framework Architecture: How It Works Without Attributes
Every Razor component inherits from `ComponentBase`. At runtime, any instance variable declared on your derived class that is not a framework primitive (`RenderHandle`, `EventCallback`, `ElementReference`) and not an injected service (`[Inject]`) is guaranteed to be **Developer Domain State**.

We intercept the lowest layers of ASP.NET Core:
1. **Component Activator Interception ([ZeroTouchComponentActivator.cs](file:///c:/Users/roman/source/repos/SocketConnectionTest/ZeroTouchComponentActivator.cs))**: Replaces Microsoft's default `IComponentActivator`. When Blazor instantiates any Razor page, we classify its domain variables using [ComponentStateClassifier.cs](file:///c:/Users/roman/source/repos/SocketConnectionTest/ComponentStateClassifier.cs), query Redis Cluster, and synchronously rehydrate its private/public variables in RAM **before `OnInitialized()` runs!**
2. **Scoped DI Service Interception ([ScopedStateExtensions.cs](file:///c:/Users/roman/source/repos/SocketConnectionTest/ScopedStateExtensions.cs))**: If developers store state inside Scoped Dependency Injected View Models (`ShoppingCartService`), registering them via `AddScopedState<T>()` marks them for automatic rehydration when ASP.NET Core creates the circuit DI container on a new pod!
3. **Unified Checkpointing ([UniversalZeroTouchCircuitHandler.cs](file:///c:/Users/roman/source/repos/SocketConnectionTest/UniversalZeroTouchCircuitHandler.cs))**: When a pod starts shutting down or a connection drops (`OnConnectionDownAsync`), our circuit handler serializes all active components and Scoped DI services into a unified Redis snapshot in one atomic batch.

```
[Blazor RenderTree & DI Engine] ──► Instantiates Component or Scoped DI Service
                                            │
                                            ▼
                    [ZeroTouchComponentActivator / DI Interceptor]
                                            │
                    1. Creates C# instance in RAM
                    2. Classifies domain variables via ComponentStateClassifier
                    3. Queries Redis Cluster for previous session snapshot
                    4. Populates RAM variables BEFORE OnInitialized() runs!
                                            │
                                            ▼
                    [Component & DI Service 100% restored! Zero attributes used!]
```

---

## 🛠️ How to Integrate into Your Blazor Application

### Step 1: Copy Library Files
Copy the `.cs` files from this repository directly into your Blazor Server project.

### Step 2: Register Services in `Program.cs`
Add the following lines to your `Program.cs`:

```csharp
using BlazorKubernetesDraining;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// 1. Register Kubernetes Circuit Draining (SIGTERM interception & tracking)
builder.Services.AddBlazorKubernetesDraining(options =>
{
    options.EnableDraining = true;
    options.DrainTimeoutSeconds = 600;
    options.PollingIntervalMilliseconds = 1000;
    options.EnableVerboseLogging = true;
});

// 2. Register Universal Zero-Touch State Preservation (Redis Cluster backed)
builder.Services.AddStackExchangeRedisCache(options =>
{
    options.Configuration = builder.Configuration.GetConnectionString("RedisCluster");
});

builder.Services.AddUniversalZeroTouchStatePreservation(options =>
{
    options.EnableStatePreservation = true;
    options.RedisKeyPrefix = "BlazorStore";
    options.SlidingExpiration = TimeSpan.FromHours(2);
});

// 3. Register Domain DI Services
// Use standard AddScoped for stateless infrastructure services:
builder.Services.AddScoped<AccountRepository>();

// Use AddScopedState for domain services / View Models that hold memory state!
builder.Services.AddScopedState<ShoppingCartService>();
builder.Services.AddScopedState<OrderWizardViewModel>();

var app = builder.Build();

app.UseRouting();

// 4. Map Kubernetes health probe endpoints (/health/ready and /health/live)
app.MapBlazorKubernetesDrainingHealthChecks();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
```

---

## 👩‍💻 The Developer Experience: 100% Invisible & Clean

Notice how clean your domain classes and Razor components remain. Developers write **zero attributes, zero interfaces, and zero manual save/load calls**:

### Scoped DI Domain Service (`ShoppingCartService.cs`)
```csharp
// Completely standard C# class. No attributes or interfaces needed!
public class ShoppingCartService
{
    public List<CartItem> Items { get; set; } = new();
    public decimal Total => Items.Sum(i => i.Price * i.Quantity);

    public void AddItem(CartItem item) => Items.Add(item);
}
```

### Razor Component (`Cart.razor`)
```razor
@page "/cart"
@inject ShoppingCartService Cart

<h3>Your Cart (@Cart.Items.Count items) - Total: $@Cart.Total</h3>

<InputText @bind-Value="_couponCode" />
<button class="btn btn-success" @onclick="ApplyCoupon">Apply Coupon</button>

@code {
    // Standard private field. Automatically checkpointed & rehydrated!
    private string _couponCode = string.Empty;

    private void ApplyCoupon()
    {
        // If Pod A dies right now during a Kubernetes rolling update,
        // Azure SignalR Service routes the user to Pod B.
        // On Pod B, ZeroTouchComponentActivator and UniversalZeroTouchCircuitHandler
        // automatically rehydrate ShoppingCartService.Items and _couponCode from Redis
        // before the page renders! The user notices zero interruption!
    }
}
```
