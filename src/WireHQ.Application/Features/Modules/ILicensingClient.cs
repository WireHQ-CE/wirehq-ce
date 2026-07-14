using WireHQ.Application.Abstractions.Licensing;

namespace WireHQ.Application.Features.Modules;

/// <summary>
/// The Community Edition's client for the hosted licensing service (docs/29-ce-marketplace-modules.md M-7):
/// <c>POST {LicensingBaseUrl}/api/v1/licensing/{activate,verify,deactivate}</c> (fronted by
/// <c>licensing.wirehq.net</c>). A CE install has no SaaS account, so the calls are anonymous — the artifacts
/// themselves are the credentials (an Ed25519-signed licence key / activation token). Every method is
/// fail-soft: a network or service error maps to an <c>Unavailable</c> outcome (never an exception), so a
/// transient outage degrades to "activation unavailable" / stays in offline grace rather than faulting the
/// control plane. CE-ONLY (overlay-added; the SaaS build ships the licensing SERVICE, not this client).
/// </summary>
public interface ILicensingClient
{
    /// <summary>Bind a licence key to this install (by fingerprint) and receive its activation token.</summary>
    Task<LicensingActivation> ActivateAsync(string licenceKey, string fingerprint, CancellationToken cancellationToken);

    /// <summary>The periodic call-home: refresh the activation token, or receive a revocation / expiry.</summary>
    Task<LicensingVerification> VerifyAsync(string activationToken, string fingerprint, CancellationToken cancellationToken);

    /// <summary>Free this install's activation slot so the licence can move to another install (best-effort).</summary>
    Task<LicensingDeactivateOutcome> DeactivateAsync(string activationToken, CancellationToken cancellationToken);
}

public enum LicensingOutcome
{
    /// <summary>The licence bound to this install; <c>ActivationToken</c> + <c>GraceEndsUtc</c> are present.</summary>
    Activated,

    /// <summary>Another install already holds this licence's single activation slot — deactivate it there first.</summary>
    SlotTaken,

    /// <summary>The licence has been revoked server-side and can no longer be activated (409, distinct from a taken slot).</summary>
    Revoked,

    /// <summary>The licensing service could not be reached, or returned an error / unparseable response.</summary>
    Unavailable,
}

public sealed record LicensingActivation(LicensingOutcome Outcome, string? ActivationToken, DateTimeOffset? GraceEndsUtc);

public enum LicensingVerifyOutcome
{
    /// <summary>The licence is healthy; a refreshed <c>ActivationToken</c> + <c>GraceEndsUtc</c> are present.</summary>
    Active,

    /// <summary>The licence has been revoked (delivered on the verify 200, D-1) — disable the module.</summary>
    Revoked,

    /// <summary>
    /// The service was reached but rejected the stored token (it has expired — the token's <c>exp</c> equals the
    /// grace boundary, so a lapsed licence's token is always expired). The caller re-activates with the licence
    /// key (idempotent for the same fingerprint) to obtain a fresh token — this is how a lapsed licence
    /// self-heals on reconnect (docs/29 M-7).
    /// </summary>
    Expired,

    /// <summary>The service could not be reached — leave the licence as-is (it stays in offline grace).</summary>
    Unavailable,
}

public sealed record LicensingVerification(
    LicensingVerifyOutcome Outcome, string? ActivationToken, DateTimeOffset? GraceEndsUtc, string? RevokeReason);

public enum LicensingDeactivateOutcome
{
    /// <summary>The server freed the activation slot — the local licence row can be removed.</summary>
    Freed,

    /// <summary>The server refused to free the slot (e.g. the self-serve move limit is reached, D-4) — keep the row.</summary>
    Refused,

    /// <summary>The service could not be reached — the deactivation is <b>unconfirmed</b>. Keep the local row and retry:
    /// removing it would delete the only local copy of the activation token while the server slot may still be bound,
    /// stranding the module (dark locally yet un-movable — a re-activation elsewhere would hit <c>SlotTaken</c>).</summary>
    Unavailable,
}

/// <summary>
/// Validates an activation token the hosted service returned — the local half of the offline trust model
/// (docs/29 M-7): the token is verified against the pinned WireHQ public keys and must be bound to THIS install's
/// fingerprint. The grace boundary the install stores is read from the verified token's <c>exp</c>, never a
/// caller-supplied field. Shared by the activate handler and the weekly verify loop.
/// </summary>
public static class ModuleTokenValidator
{
    /// <summary>
    /// The grace boundary of a valid activation token bound to <paramref name="fingerprint"/>, or <c>null</c> if
    /// the token is malformed, unverifiable (missing/bad key ring — M-18), expired, or bound to another install.
    /// </summary>
    public static DateTimeOffset? VerifiedGrace(ILicenceTokenVerifier verifier, string? activationToken, string fingerprint)
    {
        if (string.IsNullOrWhiteSpace(activationToken))
        {
            return null;
        }

        try
        {
            var verification = verifier.Verify<ActivationTokenClaims>(activationToken);
            return verification.IsValid && verification.Claims is { } claims && claims.InstanceFingerprint == fingerprint
                ? claims.GraceEndsUtc
                : null;
        }
        catch
        {
            // A missing/invalid key ring throws on first resolve (M-18) — treat as unverifiable, not a fault.
            return null;
        }
    }
}
