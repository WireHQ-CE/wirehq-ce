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

    public static NotificationPreferences CreateDefault(Guid userId) => new(Guid.CreateVersion7(), userId);

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
