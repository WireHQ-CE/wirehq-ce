using WireHQ.Domain.Common;

namespace WireHQ.Domain.Identity;

/// <summary>
/// A user's notification opt-ins, one row per user (<c>identity.notification_preferences</c>). Stored
/// preferences only — outbound notification/email flows consult them when they exist. Sensible defaults:
/// everything on except marketing.
/// </summary>
public sealed class NotificationPreferences : Entity
{
    // EF Core
    private NotificationPreferences()
    {
    }

    private NotificationPreferences(Guid id, Guid userId)
        : base(id)
    {
        UserId = userId;
        SecurityAlerts = true;
        VpnStatusAlerts = true;
        ProductAnnouncements = true;
        BillingNotifications = true;
        MarketingEmails = false;
        ServiceStatusAlerts = false;
        SmsAlerts = false; // SMS consent is OPT-IN — default OFF (docs/36-notifications.md §4/§8, TCPA)
    }

    public Guid UserId { get; private set; }

    /// <summary>Sign-ins from new devices, password/MFA changes, suspicious activity.</summary>
    public bool SecurityAlerts { get; private set; }

    /// <summary>Instance/peer health, deployments, config drift.</summary>
    public bool VpnStatusAlerts { get; private set; }

    /// <summary>New features and product updates.</summary>
    public bool ProductAnnouncements { get; private set; }

    /// <summary>Invoices, payment and plan changes.</summary>
    public bool BillingNotifications { get; private set; }

    /// <summary>Newsletters and promotional email.</summary>
    public bool MarketingEmails { get; private set; }

    /// <summary>Service status: incident and scheduled-maintenance emails (opt-in — default off).</summary>
    public bool ServiceStatusAlerts { get; private set; }

    /// <summary><b>DEPRECATED (D-3):</b> SMS consent moved to the per-(org, user) <c>SmsConsent</c> record (docs/36 §4.1)
    /// — this GLOBAL per-user flag can't express sender-specific consent, so it is no longer read or written. The column
    /// is retained inert (dropped in a later cleanup); <c>CreateDefault</c> still leaves it false. Was: SMS-alerts consent
    /// (opt-in, default OFF), the CONSENT-to-receive fact separate from phone ownership.</summary>
    public bool SmsAlerts { get; private set; }

    public static NotificationPreferences CreateDefault(Guid userId) => new(Guid.CreateVersion7(), userId);

    /// <summary><b>DEPRECATED (D-3):</b> SMS consent is now per-(org, user) via <c>SmsConsent</c> — no caller remains.
    /// Retained only so the inert column round-trips.</summary>
    public bool SetSmsAlerts(bool enabled)
    {
        if (SmsAlerts == enabled)
        {
            return false;
        }

        SmsAlerts = enabled;
        return true;
    }

    public void Update(bool securityAlerts, bool vpnStatusAlerts, bool productAnnouncements, bool billingNotifications, bool marketingEmails, bool serviceStatusAlerts)
    {
        SecurityAlerts = securityAlerts;
        VpnStatusAlerts = vpnStatusAlerts;
        ProductAnnouncements = productAnnouncements;
        BillingNotifications = billingNotifications;
        MarketingEmails = marketingEmails;
        ServiceStatusAlerts = serviceStatusAlerts;
    }
}
