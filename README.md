# Blazor Server Stateful Connection Draining & Kerberos Sidecar Architecture

This repository contains a standalone, production-ready reference library and Kubernetes deployment configuration designed to solve the problem of stateful SignalR connection disconnection and database authentication drops when restarting or scaling Blazor Server applications.

---

## 🏛️ Enterprise Architecture Perspective: Why Application-Level Interception?

While Kubernetes-level lifecycle hooks (`preStop`) are commonly used in infrastructure-driven setups, our **Enterprise Architect** identified a critical architectural principle: **framework-specific connection draining should be encapsulated within the application layer, not leaked into platform deployment manifests.**

Because ASP.NET Core SignalR's premature connection teardown is an application framework behavior, solving it in the Kubernetes manifest couples your platform orchestration to an internal .NET implementation detail. By replacing ASP.NET Core's default `IHostLifetime` with a custom **C#-level signal interceptor**, we achieve a much cleaner architectural design:

1. **Clean, Standard Kubernetes Manifests**: Your deployment YAML remains 100% standard and platform-agnostic without specialized `preStop` HTTP hooks or shell script workarounds.
2. **True Portability (Linux & Windows)**: Whether your Blazor Server app runs on Kubernetes, Docker Swarm, Windows Server IIS, Linux systemd services, or bare metal, the application self-manages its stateful connection draining automatically upon receiving OS termination signals.
3. **Flawless SignalR Preservation**: By intercepting termination signals before ASP.NET Core's hosting infrastructure sees them, `IHostApplicationLifetime.ApplicationStopping` is never triggered until all user sessions have drained out.

---

## 🖥️ Cross-Platform Signal Behavior: Windows vs. Linux

How does OS signal interception work across different operating systems?

### 1. Linux & Kubernetes Containers
* When Kubernetes terminates a pod, it sends POSIX **`SIGTERM`** (signal 15).
* Our custom [DrainingHostLifetime.cs](file:///c:/Users/roman/source/repos/SocketConnectionTest/DrainingHostLifetime.cs) catches `SIGTERM` via `PosixSignalRegistration`, sets `context.Cancel = true`, marks Readiness as Unhealthy, and waits for circuits to hit `0`.

### 2. Windows & Windows Server Containers
* On Windows, there is no direct kernel-level equivalent of POSIX `SIGTERM`. However, starting in .NET 6+, Microsoft unified cross-platform signal handling!
* When hosting on Windows or Windows Containers:
  * **Interactive Console / CMD / PowerShell**: Pressing `Ctrl+C` or `Ctrl+Break` triggers `PosixSignal.SIGINT` or `SIGQUIT`. Closing the console window triggers a Win32 `CTRL_CLOSE_EVENT`, which .NET synthesizes into `PosixSignal.SIGTERM`!
  * **Windows Containers on Kubernetes**: When Kubernetes stops a Windows container via Host Compute Service (HCS), HCS issues a `CTRL_SHUTDOWN_EVENT`. .NET catches this event and fires `PosixSignalRegistration(PosixSignal.SIGTERM)`.
  * **Result**: **Yes! You receive `SIGTERM` (or synthesized `SIGTERM`/`SIGINT`) on Windows**, and connection draining works identically!

---

## ⚡ How to Force an Immediate HARD STOP Without Draining

During emergency deployments, security incidents, or local testing, administrators may need to shut down the application immediately without waiting 10 minutes for circuits to drain. There are three built-in ways to bypass draining and force a hard stop:

### Method 1: Send a Second Signal (Double Ctrl+C / Consecutive SIGTERM)
If a termination signal (`SIGTERM` or `Ctrl+C`) arrives **a second time** while draining is already in progress, `DrainingHostLifetime` immediately aborts the graceful drain and forces an instant shutdown:
```bash
# In terminal/console:
Ctrl+C (Starts graceful drain) -> Press Ctrl+C again -> IMMEDIATE HARD SHUTDOWN!

# In Kubernetes:
kubectl delete pod blazor-app-xxxx # First SIGTERM starts drain
kubectl delete pod blazor-app-xxxx --grace-period=0 --force # Second signal forces instant kill
```

### Method 2: OS Hard Kill (`SIGKILL` / `taskkill /F`)
Operating system hard kill signals cannot be caught or blocked by any process or runtime:
* **Linux / K8s**: Sending `SIGKILL` (`kill -9 <pid>` or K8s `--force --grace-period=0`) terminates the process instantly in $0\text{ ms}$.
* **Windows**: Running `taskkill /F /IM BlazorApp.exe` invokes the Win32 `TerminateProcess()` API, shutting down instantly without draining.

### Method 3: Configuration Override (`EnableDraining = false`)
You can disable draining globally or per environment using environment variables or `appsettings.json`:
```yaml
env:
  - name: DrainingOptions__EnableDraining
    value: "false" # Bypasses draining; SIGTERM triggers immediate shutdown
  - name: DrainingOptions__DrainTimeoutSeconds
    value: "0"     # Setting timeout <= 0 also bypasses draining
```

---

## 💡 The Solution Architecture

### 1. Code-Level Interception (`DrainingHostLifetime`)
* [DrainingHostLifetime.cs](file:///c:/Users/roman/source/repos/SocketConnectionTest/DrainingHostLifetime.cs) implements ASP.NET Core's `IHostLifetime` interface, replacing Microsoft's default `ConsoleLifetime`.
* When an OS signal arrives, it sets `context.Cancel = true`, sets Readiness to Unhealthy (`503`), monitors active Blazor circuits, and only invokes `StopApplication()` once circuits drop to zero.

### 2. Active Circuit Tracking via Custom `CircuitHandler`
* [ActiveCircuitTracker.cs](file:///c:/Users/roman/source/repos/SocketConnectionTest/ActiveCircuitTracker.cs) inherits from ASP.NET Core's `CircuitHandler` and uses thread-safe counters to track open circuits.

### 3. Native Kubernetes Sidecar Containers (`initContainers` + `restartPolicy: Always`)
* Defined under `initContainers` with `restartPolicy: Always` in Kubernetes v1.28+.
* Guarantee that your Kerberos database authentication container stays running until *after* the Blazor application container has completely shut down.

### 4. Coordinated Kubernetes Health Probes
* [DrainingHealthCheck.cs](file:///c:/Users/roman/source/repos/SocketConnectionTest/DrainingHealthCheck.cs) provides Readiness (`/health/ready` returning `503` on shutdown) and Liveness (`/health/live` returning `200 OK` during drain) probes.

---

## 🚀 How to Integrate into Your Blazor Application

### Step 1: Copy the Reusable Library Files
Copy the following files directly into your Blazor Server project:
* [DrainingOptions.cs](file:///c:/Users/roman/source/repos/SocketConnectionTest/DrainingOptions.cs)
* [ActiveCircuitTracker.cs](file:///c:/Users/roman/source/repos/SocketConnectionTest/ActiveCircuitTracker.cs)
* [DrainingHealthCheck.cs](file:///c:/Users/roman/source/repos/SocketConnectionTest/DrainingHealthCheck.cs)
* [DrainingHostLifetime.cs](file:///c:/Users/roman/source/repos/SocketConnectionTest/DrainingHostLifetime.cs)
* [ServiceCollectionExtensions.cs](file:///c:/Users/roman/source/repos/SocketConnectionTest/ServiceCollectionExtensions.cs)

### Step 2: Register Services in `Program.cs`
Add the following lines to your `Program.cs` file:

```csharp
using BlazorKubernetesDraining;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// 1. Add Kubernetes Circuit Draining services, active circuit tracker, & DrainingHostLifetime
builder.Services.AddBlazorKubernetesDraining(options =>
{
    options.EnableDraining = true;
    options.DrainTimeoutSeconds = 600;
    options.PollingIntervalMilliseconds = 1000;
    options.EnableVerboseLogging = true;
});

var app = builder.Build();

app.UseRouting();

// 2. Map Kubernetes health probe endpoints (/health/ready and /health/live)
app.MapBlazorKubernetesDrainingHealthChecks();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
```
