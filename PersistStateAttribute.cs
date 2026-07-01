using System;

namespace BlazorKubernetesDraining;

/// <summary>
/// Marks a field or property on a Razor component or Scoped DI service for automatic
/// state preservation across Kubernetes pod failovers.
/// 
/// Only members decorated with this attribute are serialized to Redis and rehydrated on failover.
/// All other fields and properties are left untouched, ensuring:
/// - Explicit opt-in (no accidental PII leakage to Redis)
/// - Visible in code review (every [PersistState] is a conscious architectural decision)
/// - Greppable and debuggable (search the codebase for [PersistState] to find all persisted state)
/// - Minimal serialization surface (only what matters is checkpointed)
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
public sealed class PersistStateAttribute : Attribute { }
