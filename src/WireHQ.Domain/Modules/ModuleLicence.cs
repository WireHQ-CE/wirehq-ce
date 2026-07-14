using WireHQ.Domain.Common;

namespace WireHQ.Domain.Modules;

/// <summary>
/// A CE Marketplace module licence the operator has activated on this install (docs/29-ce-marketplace-modules.md
/// M-5/M-7). Persisted in the platform-global <c>modules</c> schema — install-scoped, NOT tenant-scoped (no
/// <c>organization_id</c>, so it is outside row-level security; a licence is per install, not per org). The
/// install stores the verified licence KEY (the module slug is read from its <c>mod</c> claim — the activation
/// token lacks it, M-6) plus the hosted service's <b>activation token</b> (the artifact that gates day-to-day
/// validity) and the offline-grace boundary (<see cref="GraceEndsUtc"/>, the token's <c>exp</c>). A weekly
/// call-home refreshes the token + grace and applies revocation; past grace the capability locks
/// (nag-don't-kill — the evaluator derives that, the control plane never faults).
///
/// <para>CE-ONLY: this entity is overlay-added, so it exists only in the generated Community Edition and is
/// absent from the SaaS model (which never carries the <c>modules</c> schema).</para>
/// </summary>
public sealed class ModuleLicence : Entity
{
    // EF Core
    private ModuleLicence()
    {
    }

    private ModuleLicence(
        Guid id, string moduleSlug, string licenceId, string licenceKey,
        string activationToken, DateTimeOffset graceEndsUtc, DateTimeOffset now)
        : base(id)
    {
        ModuleSlug = moduleSlug;
        LicenceId = licenceId;
        LicenceKey = licenceKey;
        Status = ModuleLicenceStatus.Active;
        ActivationToken = activationToken;
        GraceEndsUtc = graceEndsUtc;
        LastVerifiedAtUtc = now;
        ActivatedAtUtc = now;
    }

    /// <summary>The module slug this licence unlocks (the licence key's <c>mod</c> claim). Unique per install.</summary>
    public string ModuleSlug { get; private set; } = null!;

    /// <summary>The server-side licence id (<c>lid</c>) — the reference the call-home verify/deactivate uses.</summary>
    public string LicenceId { get; private set; } = null!;

    /// <summary>
    /// The signed licence key the operator entered. Retained so the install can re-read its <c>mod</c> claim and
    /// re-activate with the hosted service; the key is authenticity-proof on its own (verified offline against
    /// the pinned public keys) and carries no secret.
    /// </summary>
    public string LicenceKey { get; private set; } = null!;

    /// <summary>Whether the licence currently grants its capability (docs/29 M-7).</summary>
    public ModuleLicenceStatus Status { get; private set; }

    /// <summary>
    /// The hosted service's activation token bound to this install's fingerprint (<c>fp</c>). Sent on each
    /// call-home verify + on deactivate; a fresh one is stored on every successful re-verify.
    /// </summary>
    public string ActivationToken { get; private set; } = null!;

    /// <summary>
    /// The offline-grace hard boundary — the activation token's <c>exp</c>. Past it the capability locks until a
    /// re-verify re-grants it (nag-don't-kill); the weekly call-home refreshes it well before then.
    /// </summary>
    public DateTimeOffset GraceEndsUtc { get; private set; }

    /// <summary>When the install last successfully re-verified this licence with the hosted service.</summary>
    public DateTimeOffset LastVerifiedAtUtc { get; private set; }

    /// <summary>When the operator first activated this module on the install.</summary>
    public DateTimeOffset ActivatedAtUtc { get; private set; }

    /// <summary>
    /// Records a fresh activation of <paramref name="moduleSlug"/>: the verified licence key, the hosted service's
    /// activation token, and the grace boundary read from that (locally-verified) token.
    /// </summary>
    public static ModuleLicence Activate(
        string moduleSlug, string licenceId, string licenceKey,
        string activationToken, DateTimeOffset graceEndsUtc, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleSlug);
        ArgumentException.ThrowIfNullOrWhiteSpace(licenceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(licenceKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(activationToken);
        return new ModuleLicence(Guid.CreateVersion7(), moduleSlug.Trim(), licenceId.Trim(), licenceKey.Trim(),
            activationToken, graceEndsUtc, now);
    }

    /// <summary>
    /// Re-licenses this (revoked) module in place with a fresh licence — used when the operator activates a
    /// replacement key for a module whose prior licence the service revoked (the row is retained so the evaluator
    /// keeps the feature locked, so it is updated rather than a second row inserted against the unique slug index).
    /// </summary>
    public void Reactivate(
        string licenceId, string licenceKey, string activationToken, DateTimeOffset graceEndsUtc, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(licenceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(licenceKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(activationToken);
        LicenceId = licenceId.Trim();
        LicenceKey = licenceKey.Trim();
        ActivationToken = activationToken;
        GraceEndsUtc = graceEndsUtc;
        LastVerifiedAtUtc = now;
        ActivatedAtUtc = now;
        Status = ModuleLicenceStatus.Active;
    }

    /// <summary>
    /// A successful call-home re-verify: refresh the activation token + grace boundary and mark the licence
    /// active (a re-verify — or a re-activation after the token expired — re-grants a licence that had lapsed).
    /// </summary>
    public void RecordVerification(string activationToken, DateTimeOffset graceEndsUtc, DateTimeOffset verifiedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(activationToken);
        ActivationToken = activationToken;
        GraceEndsUtc = graceEndsUtc;
        LastVerifiedAtUtc = verifiedAt;
        Status = ModuleLicenceStatus.Active;
    }

    /// <summary>The hosted service reported the licence revoked (delivered on the verify 200, D-1) — disable it.</summary>
    public void Revoke() => Status = ModuleLicenceStatus.Revoked;
}
