using FluentAssertions;
using WireHQ.Domain.Identity;
using Xunit;

namespace WireHQ.Domain.UnitTests.Identity;

/// <summary>SMS-alerts consent defaults + toggle semantics (docs/36-notifications.md §4/§8): opt-in, default OFF, and a
/// change signal so the handler only writes an immutable consent event on a REAL toggle.</summary>
public sealed class NotificationPreferencesTests
{
    [Fact]
    public void Sms_alerts_default_off()
    {
        var prefs = NotificationPreferences.CreateDefault(Guid.NewGuid());

        prefs.SmsAlerts.Should().BeFalse("SMS consent is opt-in (TCPA) — default OFF");
    }

    [Fact]
    public void SetSmsAlerts_reports_change_only_when_the_value_moves()
    {
        var prefs = NotificationPreferences.CreateDefault(Guid.NewGuid());

        prefs.SetSmsAlerts(true).Should().BeTrue("off → on is a real change");
        prefs.SmsAlerts.Should().BeTrue();

        prefs.SetSmsAlerts(true).Should().BeFalse("on → on is a no-op, so no consent event is written");

        prefs.SetSmsAlerts(false).Should().BeTrue("on → off is a real change (opt-out is also an audited consent event)");
        prefs.SmsAlerts.Should().BeFalse();
    }

    [Fact]
    public void General_update_does_not_touch_sms_consent()
    {
        var prefs = NotificationPreferences.CreateDefault(Guid.NewGuid());
        prefs.SetSmsAlerts(true);

        // The bundled preferences save must NOT be able to flip SMS consent — consent is a separate, audited action.
        prefs.Update(securityAlerts: false, vpnStatusAlerts: false, productAnnouncements: false,
            billingNotifications: false, marketingEmails: true, serviceStatusAlerts: true);

        prefs.SmsAlerts.Should().BeTrue("SmsAlerts is not a parameter of Update()");
    }
}
