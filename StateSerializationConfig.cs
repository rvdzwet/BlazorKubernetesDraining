using System.Text.Json;
using System.Text.Json.Serialization;

namespace BlazorKubernetesDraining;

/// <summary>
/// Centralized, enterprise-resilient JSON serialization configuration for Universal Zero-Touch State Preservation.
/// Prevents serialization failures caused by circular object references, null properties, or case mismatch.
/// </summary>
public static class StateSerializationConfig
{
    /// <summary>
    /// Bulletproof JsonSerializerOptions configured for domain state snapshotting and rehydration.
    /// </summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        // Ignore circular references (e.g., Order -> OrderItem -> Order) instead of throwing JsonException
        ReferenceHandler = ReferenceHandler.IgnoreCycles,

        // Be tolerant of property renaming or casing differences across application version updates
        PropertyNameCaseInsensitive = true,

        // Ignore null values to reduce Redis memory footprint and avoid overwriting default values with null
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,

        // Limit maximum graph depth to prevent stack overflow from pathological object trees
        MaxDepth = 64,

        // Allow reading trailing commas and comments if present in snapshots
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };
}
