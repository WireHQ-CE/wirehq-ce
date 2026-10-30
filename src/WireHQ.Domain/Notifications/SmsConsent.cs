using WireHQ.Domain.Common;

namespace WireHQ.Domain.Notifications;

/// <summary>
/// A user's <b>per-(organisation, user)</b> SMS-alerts consent (docs/36-notifications.md §4.1, Wave 4 Slice D
/// precondition D-3). SMS consent is legally <b>sender-specific</b> — the operator running each org is the sender of
/// record — so consent given in org A must never authorise a send in org B. This replaces the old global per-user
/// <c>NotificationPreferences.SmsAlerts</c> flag as the queryable consent state; the immutable
/// <c>notifications.sms_consent_changed</c> audit event remains the durable legal proof (it already records the org +
/// disclosure text/version + IP).
/// <para>
/// "<b>Currently consented</b>" is simply <see cref="Consented"/> == true (a user with no row is not consented ⇒
/// default-OFF). Tenant-owned in the reused <c>identity</c> schema (carries <c>organization_id</c> ⇒ RLS-covered
/// automatically); consent is a hard precondition of the SMS audience on top of phone <i>ownership</i>
/// (<see cref="WireHQ.Domain.Identity.User.PhoneVerified"/>) and the absence of an active suppression.
/// </para>
/// </summary>
public sealed class SmsConsent : Entity, ITenantOwned
{
    // EF Core
    private SmsConsent()
    {
    }

    private SmsConsent(Guid id, Guid organizationId, Guid userId, bool consented, string disclosureVersion, DateTimeOffset nowUtc)
        : base(id)
    {
        OrganizationId = organizationId;
        UserId = userId;
        Apply(consented, disclosureVersion, nowUtc);
    }

    public Guid OrganizationId { get; private set; }

    public Guid UserId { get; private set; }

    /// <summary>The current consent state for this (org, user). True = opted in to critical SMS from this org.</summary>
    public bool Consented { get; private set; }

    /// <summary>When consent was last granted (the current opt-in's instant); null if never opted in.</summary>
    public DateTimeOffset? ConsentedAtUtc { get; private set; }

    /// <summary>When consent was last revoked; null while currently consented (or never opted in).</summary>
    public DateTimeOffset? RevokedAtUtc { get; private set; }

    /// <summary>The disclosure version the user agreed to at the last opt-in (for proof alongside the audit event).</summary>
    public string? DisclosureVersion { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    /// <summary>Create the (org, user) consent record at its first toggle.</summary>
    public static SmsConsent Create(Guid organizationId, Guid userId, bool consented, string disclosureVersion, DateTimeOffset nowUtc) =>
        new(Guid.CreateVersion7(), organizationId, userId, consented, disclosureVersion, nowUtc);

    /// <summary>Set the consent state. Returns true only when the value actually changed, so the caller writes an
    /// immutable consent audit event on a real toggle (docs/36 §4.1) — mirrors the old <c>SetSmsAlerts</c> contract.</summary>
    public bool Set(bool consented, string disclosureVersion, DateTimeOffset nowUtc)
    {
        if (Consented == consented)
        {
            return false;
        }

        Apply(consented, disclosureVersion, nowUtc);
        return true;
    }

    private void Apply(bool consented, string disclosureVersion, DateTimeOffset nowUtc)
    {
        Consented = consented;
        UpdatedAtUtc = nowUtc;
        if (consented)
        {
            ConsentedAtUtc = nowUtc;
            RevokedAtUtc = null;
            DisclosureVersion = disclosureVersion;
        }
        else
        {
            RevokedAtUtc = nowUtc;
        }
    }
}
