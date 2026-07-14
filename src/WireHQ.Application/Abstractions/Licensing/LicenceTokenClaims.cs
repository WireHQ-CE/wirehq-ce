using System.Text.Json.Serialization;

namespace WireHQ.Application.Abstractions.Licensing;

/// <summary>
/// The claims inside a <b>licence key</b> — the artifact a buyer receives by email and enters into their
/// Community Edition (docs/19-marketplace-licensing.md §3). Its signature proves authenticity fully
/// offline; a licence key deliberately has no <c>exp</c> (it never expires cryptographically) — the
/// business layer enforces the update window (<see cref="UpdateWindowEndUtc"/>). The signing key id
/// lives in the token footer, not here.
/// </summary>
public sealed record LicenceKeyClaims
{
    /// <summary>The licence id (<c>lid</c>) — the server-side <c>marketplace.licences</c> row.</summary>
    [JsonPropertyName("lid")]
    public required string LicenceId { get; init; }

    /// <summary>The module slug (<c>mod</c>) this key unlocks (e.g. <c>remote-deployment</c>).</summary>
    [JsonPropertyName("mod")]
    public required string ModuleSlug { get; init; }

    /// <summary>A hash of the buyer's email (<c>bhash</c>) — links the key to the purchaser without embedding PII.</summary>
    [JsonPropertyName("bhash")]
    public required string BuyerEmailHash { get; init; }

    /// <summary>When the key was issued (<c>iat</c>).</summary>
    [JsonPropertyName("iat")]
    public required DateTimeOffset IssuedAtUtc { get; init; }

    /// <summary>End of the included update window (<c>uwe</c>); past it the module keeps working but stops updating.</summary>
    [JsonPropertyName("uwe")]
    public required DateTimeOffset UpdateWindowEndUtc { get; init; }
}

/// <summary>
/// The claims inside an <b>activation token</b> — what the licensing service returns when a licence key
/// is activated (or re-verified) for a specific install, and what actually gates the module day-to-day
/// (docs/19-marketplace-licensing.md §3/§5). It binds the licence to one instance fingerprint and carries
/// the re-verification schedule. <see cref="GraceEndsUtc"/> is the registered <c>exp</c> claim, so an
/// activation token past its grace is rejected by the verifier itself.
/// </summary>
public sealed record ActivationTokenClaims
{
    /// <summary>The licence id (<c>lid</c>) this activation is for.</summary>
    [JsonPropertyName("lid")]
    public required string LicenceId { get; init; }

    /// <summary>The install fingerprint (<c>fp</c>) this token is bound to — copying it to another install is useless.</summary>
    [JsonPropertyName("fp")]
    public required string InstanceFingerprint { get; init; }

    /// <summary>When the token was issued (<c>iat</c>).</summary>
    [JsonPropertyName("iat")]
    public required DateTimeOffset IssuedAtUtc { get; init; }

    /// <summary>When the install should next call home to re-verify (<c>nvb</c>) — a soft signal, before <c>exp</c>.</summary>
    [JsonPropertyName("nvb")]
    public required DateTimeOffset NextVerifyByUtc { get; init; }

    /// <summary>
    /// The hard boundary (<c>exp</c>): the end of the offline grace window (D-1). Past this the verifier
    /// rejects the token, so the module must have re-verified by now (nag-don't-kill still applies at the
    /// business layer — a rejected token degrades, it does not delete a working tunnel).
    /// </summary>
    [JsonPropertyName("exp")]
    public required DateTimeOffset GraceEndsUtc { get; init; }

    /// <summary>
    /// Set when the licence has been revoked (<c>rev</c>) — the licensing service delivers this to tell an
    /// online install to disable the module after its notice period. Absent on healthy tokens.
    /// </summary>
    [JsonPropertyName("rev")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Revoked { get; init; }
}
