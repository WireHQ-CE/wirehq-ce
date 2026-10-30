using Microsoft.EntityFrameworkCore;
using WireHQ.Application.Abstractions;
using WireHQ.Application.Abstractions.Persistence;
using WireHQ.Application.Common.Messaging;
using WireHQ.Application.Entitlements;
using WireHQ.Application.Features.Notifications;
using WireHQ.Domain.Auditing;
using WireHQ.Domain.Identity;
using WireHQ.Domain.Notifications;
using WireHQ.Shared.Results;

namespace WireHQ.Application.Features.Account.Notifications;

// The signed-in user's notification opt-ins. Stored per user; created on first read with sensible
// defaults. These are preferences only — outbound notification/email flows consult them when they exist.

public sealed record NotificationPreferencesResponse(
    bool SecurityAlerts,
    bool VpnStatusAlerts,
    bool ProductAnnouncements,
    bool BillingNotifications,
    bool MarketingEmails,
    bool ServiceStatusAlerts,
    // SMS-alerts consent (docs/36 §8) — read-only here; changed only via the dedicated, audited consent toggle below.
    bool SmsAlerts);

/// <summary>Returns the current user's notification preferences (defaults if none saved yet).</summary>
public sealed record GetNotificationPreferencesQuery : IQuery<NotificationPreferencesResponse>;

public sealed class GetNotificationPreferencesQueryHandler(
    IApplicationDbContext dbContext, ICurrentUser currentUser, ITenantContext tenant)
    : IQueryHandler<GetNotificationPreferencesQuery, NotificationPreferencesResponse>
{
    private static readonly Error NotAuthenticated = Error.Unauthorized("auth.unauthenticated", "Authentication is required.");

    public async Task<Result<NotificationPreferencesResponse>> Handle(GetNotificationPreferencesQuery query, CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId)
        {
            return NotAuthenticated;
        }

        var prefs = await dbContext.NotificationPreferences.AsNoTracking()
            .FirstOrDefaultAsync(p => p.UserId == userId, cancellationToken)
            ?? NotificationPreferences.CreateDefault(userId);

        // SMS consent is PER-(org, user) (docs/36 §4.1, D-3) — read it for the ACTIVE org, not the (deprecated) global
        // preferences flag, so the toggle reflects consent for the org the user is currently in. No active org ⇒ off.
        var smsConsent = tenant.OrganizationId is { } organizationId
            && await dbContext.SmsConsents.AsNoTracking()
                .AnyAsync(c => c.OrganizationId == organizationId && c.UserId == userId && c.Consented, cancellationToken);

        return new NotificationPreferencesResponse(
            prefs.SecurityAlerts, prefs.VpnStatusAlerts, prefs.ProductAnnouncements,
            prefs.BillingNotifications, prefs.MarketingEmails, prefs.ServiceStatusAlerts, smsConsent);
    }
}

/// <summary>Updates (or creates) the current user's notification preferences.</summary>
public sealed record UpdateNotificationPreferencesCommand(
    bool SecurityAlerts,
    bool VpnStatusAlerts,
    bool ProductAnnouncements,
    bool BillingNotifications,
    bool MarketingEmails,
    bool ServiceStatusAlerts) : ICommand;

public sealed class UpdateNotificationPreferencesCommandHandler(
    IApplicationDbContext dbContext,
    ICurrentUser currentUser,
    IAuditWriter audit)
    : ICommandHandler<UpdateNotificationPreferencesCommand>
{
    private static readonly Error NotAuthenticated = Error.Unauthorized("auth.unauthenticated", "Authentication is required.");

    public async Task<Result> Handle(UpdateNotificationPreferencesCommand command, CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId)
        {
            return NotAuthenticated;
        }

        var prefs = await dbContext.NotificationPreferences.FirstOrDefaultAsync(p => p.UserId == userId, cancellationToken);
        if (prefs is null)
        {
            prefs = NotificationPreferences.CreateDefault(userId);
            dbContext.NotificationPreferences.Add(prefs);
        }

        prefs.Update(command.SecurityAlerts, command.VpnStatusAlerts, command.ProductAnnouncements,
            command.BillingNotifications, command.MarketingEmails, command.ServiceStatusAlerts);

        audit.Record("account.notifications_updated", AuditOutcome.Success, nameof(NotificationPreferences), userId.ToString());
        return Result.Success();
    }
}

// --- SMS-alerts consent (docs/36 §4/§8, SMS Wave 4) ---

/// <summary>Toggle SMS-alerts consent for the signed-in user. A deliberate, separately-AUDITED action (NOT bundled into
/// the general preferences save): consent is an immutable fact distinct from phone ownership (docs/36 §4.1). Every real
/// toggle writes an immutable consent event capturing the exact disclosure text + version shown; the actor, org and
/// source IP are enriched onto the audit entry automatically.</summary>
public sealed record SetSmsAlertsConsentCommand(bool Enabled) : ICommand, IRequiresFeature
{
    // Gated on notifications.sms for consistency with Start/Confirm/SetSmsProvider — a non-entitled org can't send SMS
    // anyway (the audience resolver re-checks entitlement at dispatch), so consent is moot without the module.
    public string RequiredFeature => PlanFeatures.NotificationsSms;
}

public sealed class SetSmsAlertsConsentCommandHandler(
    IApplicationDbContext dbContext,
    ICurrentUser currentUser,
    ITenantContext tenant,
    IDateTimeProvider clock,
    IAuditWriter audit)
    : ICommandHandler<SetSmsAlertsConsentCommand>
{
    private static readonly Error NotAuthenticated = Error.Unauthorized("auth.unauthenticated", "Authentication is required.");
    private static readonly Error NoOrg = Error.Forbidden("auth.tenant_required", "No active organization.");

    public async Task<Result> Handle(SetSmsAlertsConsentCommand command, CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId)
        {
            return NotAuthenticated;
        }

        // SMS consent is PER-(org, user) (docs/36 §4.1, D-3): consent given here authorises ONLY this org's sends —
        // it can never carry over to another org the user also belongs to (the operator is the sender of record).
        if (tenant.OrganizationId is not { } organizationId)
        {
            return NoOrg;
        }

        var consent = await dbContext.SmsConsents
            .FirstOrDefaultAsync(c => c.OrganizationId == organizationId && c.UserId == userId, cancellationToken);

        bool changed;
        if (consent is null)
        {
            // No row ⇒ not consented (default-OFF). Disabling is a no-op; enabling creates the consenting row.
            if (!command.Enabled)
            {
                return Result.Success();
            }

            dbContext.SmsConsents.Add(SmsConsent.Create(
                organizationId, userId, consented: true, SmsNotificationDisclosure.DisclosureVersion, clock.UtcNow));
            changed = true;
        }
        else
        {
            changed = consent.Set(command.Enabled, SmsNotificationDisclosure.DisclosureVersion, clock.UtcNow);
        }

        // Only record a consent event on a REAL change. The notifications.* action is un-routable by the self-loop
        // denylist, and the audit log is append-only (RLS + hash chain) → the consent fact is immutable (docs/36 §4.1).
        if (changed)
        {
            audit.Record("notifications.sms_consent_changed", AuditOutcome.Success, nameof(SmsConsent), userId.ToString(),
                new
                {
                    command.Enabled,
                    OrganizationId = organizationId,
                    DisclosureVersion = SmsNotificationDisclosure.DisclosureVersion,
                    DisclosureText = SmsNotificationDisclosure.DisclosureText,
                });
        }

        return Result.Success();
    }
}
