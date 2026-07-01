using Microsoft.AspNetCore.Http;

namespace BlazorKubernetesDraining;

/// <summary>
/// Scoped service that resolves and caches the current user's session identity for the lifetime
/// of the Blazor circuit. Provides a stable session ID for Redis key generation.
///
/// Resolution Order:
/// 1. Authenticated user claim "SessionId" (custom claim from your auth pipeline).
/// 2. ASP.NET Core Authentication session cookie identifier.
/// 3. Null (no session identity available — state preservation is skipped).
///
/// This is registered as Scoped so the singleton PersistStateComponentActivator can resolve it
/// through the service provider without capturing a scoped dependency directly.
/// </summary>
public class SessionIdentityProvider
{
    public string? SessionId { get; }

    public SessionIdentityProvider(IHttpContextAccessor httpContextAccessor)
    {
        var httpContext = httpContextAccessor.HttpContext;

        // 1. Prefer an explicit SessionId claim from your authentication middleware
        SessionId = httpContext?.User.FindFirst("SessionId")?.Value;

        // 2. Fall back to the authenticated user identity name (e.g., Kerberos UPN / Windows principal)
        if (SessionId == null)
        {
            SessionId = httpContext?.User.Identity?.Name;
        }

        // 3. If no authenticated identity, session-based state preservation is not possible.
        // The activator and circuit handler will skip rehydration/checkpointing gracefully.
    }
}
