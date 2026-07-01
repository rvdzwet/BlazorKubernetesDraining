# Blazor Server Stateful Connection Draining & [PersistState] State Preservation

Production-ready library for ASP.NET Core Blazor Server applications hosted on Kubernetes, solving:

1. **Stateful SignalR connection drops** during rolling updates (C#-level `SIGTERM` interception).
2. **In-memory state loss** across pod failovers (explicit `[PersistState]` attribute + Redis).

---

## 🏛️ Part 1: C#-Level SIGTERM Interception & Circuit Draining

Replaces ASP.NET Core's default `IHostLifetime` with [DrainingHostLifetime.cs](file:///c:/Users/roman/source/repos/SocketConnectionTest/DrainingHostLifetime.cs) to intercept OS termination signals at the application level, mark Readiness probes as Unhealthy, and delay framework shutdown until all Blazor circuits drain to zero.

**Cross-Platform**: Works on Linux (POSIX `SIGTERM`), Windows (.NET 6+ synthesizes `CTRL_CLOSE_EVENT` → `PosixSignal.SIGTERM`), and Windows Server Containers (HCS `CTRL_SHUTDOWN_EVENT`).

**Force Hard Stop**: Double `Ctrl+C`, `SIGKILL`/`taskkill /F`, or set `DrainingOptions__EnableDraining=false`.

---

## 🚀 Part 2: [PersistState] Attribute-Based State Preservation

### Why Explicit Opt-In?
Implicit "zero-touch" state serialization is architecturally dangerous in enterprise codebases:
- **Security**: Any private field silently leaks to Redis without security review or GDPR consideration.
- **Deny-list fragility**: Heuristic type filtering will always have gaps that crash at runtime.
- **Debugging**: When stale state appears, there is no annotation trail to trace what's persisted.
- **Performance**: Every component queries Redis, even stateless layout/nav components.

The `[PersistState]` attribute gives developers **explicit, visible, reviewable** control over what survives a pod failover.

### Architecture

```
[Circuit Opens on Pod B (failover)]
        │
        ▼
[PersistStateCircuitHandler.OnCircuitOpenedAsync]
        │
        ├── ONE Redis read: loads all [PersistState] member values
        ├── Stores snapshot in singleton CircuitStateCache (in-memory dictionary)
        └── Rehydrates Scoped DI services directly
        │
        ▼
[PersistStateComponentActivator.CreateInstance]  (called per component)
        │
        ├── Checks ComponentStateClassifier cache → 0 [PersistState] members? Skip (fast path)
        ├── Reads from in-memory CircuitStateCache (dictionary lookup, zero network I/O)
        └── Sets [PersistState] fields/properties via reflection
        │
        ▼
[Component renders with restored state. OnInitialized() sees populated values.]

---

[Connection drops / pod terminates]
        │
        ▼
[PersistStateCircuitHandler.OnConnectionDownAsync]
        │
        ├── ONE batched Redis write: all tracked component [PersistState] members
        └── ONE batched Redis write: all Scoped DI service [PersistState] members

---

[Circuit closes (user navigates away or session expires)]
        │
        ▼
[PersistStateCircuitHandler.OnCircuitClosedAsync]
        │
        └── Evicts session from CircuitStateCache (prevents memory leak)
```

### Performance Characteristics

| Operation | Cost | When |
|:---|:---|:---|
| Component with 0 `[PersistState]` members | Array length check (cached) | Every component create |
| Component with N `[PersistState]` members | N dictionary lookups | Component create |
| Redis read | 1 call per circuit open | Circuit open only |
| Redis write | 2 calls per connection drop | Connection drop only |
| Memory cleanup | 1 dictionary remove | Circuit close |

---

## 🛠️ Integration

### Program.cs
```csharp
using BlazorKubernetesDraining;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// 1. SIGTERM interception & circuit draining
builder.Services.AddBlazorKubernetesDraining(options =>
{
    options.DrainTimeoutSeconds = 600;
});

// 2. Redis cache (prerequisite for state preservation)
builder.Services.AddStackExchangeRedisCache(options =>
{
    options.Configuration = builder.Configuration.GetConnectionString("RedisCluster");
});

// 3. [PersistState] attribute-based state preservation
builder.Services.AddPersistStatePreservation(options =>
{
    options.RedisKeyPrefix = "BlazorStore";
    options.SlidingExpiration = TimeSpan.FromHours(2);
});

// 4. Stateful DI services (use AddScopedState instead of AddScoped)
builder.Services.AddScopedState<ShoppingCartService>();

var app = builder.Build();
app.UseRouting();
app.MapBlazorKubernetesDrainingHealthChecks();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.Run();
```

### Razor Component
```razor
@page "/order-wizard"

<h3>Step @_currentStep: Customer Information</h3>
<InputText @bind-Value="_customerName" />
<button @onclick="NextStep">Next</button>

@code {
    [PersistState]
    private string _customerName = string.Empty;

    [PersistState]
    private int _currentStep = 1;

    // NOT persisted — transient UI state resets on failover (intentional)
    private bool _isDropdownOpen = false;

    private void NextStep() => _currentStep++;
}
```

### Scoped DI Service
```csharp
public class ShoppingCartService
{
    [PersistState]
    public List<CartItem> Items { get; set; } = new();

    // NOT persisted — computed property, recalculated from Items
    public decimal Total => Items.Sum(i => i.Price * i.Quantity);
}
```

---

## 📂 File Manifest

| File | Role |
|:---|:---|
| [PersistStateAttribute.cs](file:///c:/Users/roman/source/repos/SocketConnectionTest/PersistStateAttribute.cs) | Opt-in marker attribute for fields/properties |
| [ComponentStateClassifier.cs](file:///c:/Users/roman/source/repos/SocketConnectionTest/ComponentStateClassifier.cs) | Cached reflection scanner for `[PersistState]` members |
| [CircuitStateCache.cs](file:///c:/Users/roman/source/repos/SocketConnectionTest/CircuitStateCache.cs) | Singleton in-memory staging area (eliminates per-component Redis calls) |
| [SessionIdentityProvider.cs](file:///c:/Users/roman/source/repos/SocketConnectionTest/SessionIdentityProvider.cs) | Scoped resolver for stable user session identity |
| [PersistStateComponentActivator.cs](file:///c:/Users/roman/source/repos/SocketConnectionTest/ZeroTouchComponentActivator.cs) | `IComponentActivator` replacement (reads from memory, not Redis) |
| [PersistStateCircuitHandler.cs](file:///c:/Users/roman/source/repos/SocketConnectionTest/UniversalZeroTouchCircuitHandler.cs) | Batched Redis I/O on circuit open/close + memory cleanup |
| [ScopedStateExtensions.cs](file:///c:/Users/roman/source/repos/SocketConnectionTest/ScopedStateExtensions.cs) | `AddScopedState<T>()` for DI services with `[PersistState]` members |
| [StateSerializationConfig.cs](file:///c:/Users/roman/source/repos/SocketConnectionTest/StateSerializationConfig.cs) | Resilient JSON options (cycle handling, case tolerance) |
| [StatePreservationOptions.cs](file:///c:/Users/roman/source/repos/SocketConnectionTest/StatePreservationOptions.cs) | Configuration (Redis prefix, expiration, diagnostics) |
| [DrainingHostLifetime.cs](file:///c:/Users/roman/source/repos/SocketConnectionTest/DrainingHostLifetime.cs) | `IHostLifetime` SIGTERM interceptor |
| [ActiveCircuitTracker.cs](file:///c:/Users/roman/source/repos/SocketConnectionTest/ActiveCircuitTracker.cs) | Thread-safe circuit counter |
| [DrainingHealthCheck.cs](file:///c:/Users/roman/source/repos/SocketConnectionTest/DrainingHealthCheck.cs) | Readiness (503 on drain) and Liveness (always 200) probes |
| [DrainingOptions.cs](file:///c:/Users/roman/source/repos/SocketConnectionTest/DrainingOptions.cs) | Draining configuration |
| [ServiceCollectionExtensions.cs](file:///c:/Users/roman/source/repos/SocketConnectionTest/ServiceCollectionExtensions.cs) | `AddBlazorKubernetesDraining()` + `AddPersistStatePreservation()` |
| [ScopedComponentStateRegistry.cs](file:///c:/Users/roman/source/repos/SocketConnectionTest/ScopedComponentStateRegistry.cs) | WeakReference-based component tracking per circuit |
| [kubernetes-deployment.yaml](file:///c:/Users/roman/source/repos/SocketConnectionTest/kubernetes-deployment.yaml) | Clean K8s manifest with native Kerberos sidecar |

---

## 📄 License

MIT License (with Teun Kooijman Exclusion Clause). See [LICENSE](file:///c:/Users/roman/source/repos/SocketConnectionTest/LICENSE).

> [!CAUTION]
> **EXPLICIT EXCLUSION**: This license is **NOT GRANTED to Teun Kooijman** under any circumstances.
