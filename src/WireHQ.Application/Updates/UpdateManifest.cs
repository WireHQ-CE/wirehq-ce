using System.Text.Json.Serialization;

namespace WireHQ.Application.Updates;

/// <summary>How urgent a release is. Trusted only because the manifest is signature-verified (docs/30 U-4).
/// Serialized as its string name; read case-insensitively with an UNKNOWN value degrading to <c>None</c> rather
/// than rejecting the whole manifest (docs/30 U-13a — a forward-compat guard so a future severity tier never
/// blinds an old install to a security release; loudness comes from security/unsupported, not severity).</summary>
[JsonConverter(typeof(LenientUpdateSeverityConverter))]
public enum UpdateSeverity
{
    None,
    Low,
    Medium,
    High,
    Critical,
}

/// <summary>
/// The payload WireHQ publishes (as a signed PASETO token, docs/30 §5) describing the latest available WireHQ
/// version. A Community Edition install verifies the signature against a pinned update public key, then compares
/// <see cref="LatestVersion"/> to its own build-stamped version. The update <em>command</em> and the release link
/// are NEVER taken from here (they are fixed/constructed in the client) — only version + flags + an optional
/// untrusted vendor message. (docs/30 U-3/U-4)
/// </summary>
public sealed record UpdateManifest
{
    /// <summary>The latest available version, SemVer core (e.g. <c>0.41.0</c>).</summary>
    [JsonPropertyName("latestVersion")]
    public required string LatestVersion { get; init; }

    [JsonPropertyName("releasedAtUtc")]
    public DateTimeOffset? ReleasedAtUtc { get; init; }

    /// <summary>True if this release fixes a security issue → the notification is loud + non-dismissible.</summary>
    [JsonPropertyName("security")]
    public bool Security { get; init; }

    [JsonPropertyName("severity")]
    public UpdateSeverity Severity { get; init; }

    /// <summary>True if the release carries a database migration → the modal shows a "back up first" callout.</summary>
    [JsonPropertyName("requiresMigration")]
    public bool RequiresMigration { get; init; }

    /// <summary>Below this, the running version is "no longer supported" (its own tone, not a security cry-wolf).</summary>
    [JsonPropertyName("minSupportedVersion")]
    public string? MinSupportedVersion { get; init; }

    /// <summary>An optional short human note. UNTRUSTED even when signature-verified — rendered length-capped,
    /// escaped, link-free, visually distinct from the actionable command (docs/30 U-4).</summary>
    [JsonPropertyName("summary")]
    public string? Summary { get; init; }
}
