using FluentAssertions;
using WireHQ.Application.Features.Notifications;
using Xunit;

namespace WireHQ.Application.UnitTests.Notifications;

/// <summary>
/// The disclosure-budget renderer (docs/36-notifications.md §4.2): every message reserves room for the sender identity
/// + the "Reply STOP to opt out" notice, so the opt-out instruction is NEVER crowded out by truncation — the variable
/// body is truncated instead. The consent disclosure text/version are constants recorded on the consent event.
/// </summary>
public sealed class SmsNotificationDisclosureTests
{
    [Fact]
    public void Verification_body_carries_sender_identity_and_stop_notice()
    {
        var body = SmsNotificationDisclosure.RenderVerificationBody("123456");

        body.Should().StartWith(SmsNotificationDisclosure.SenderIdentity);
        body.Should().Contain("123456");
        body.Should().Contain(SmsNotificationDisclosure.StopNotice);
        body.Length.Should().BeLessThanOrEqualTo(SmsNotificationDisclosure.SingleSegmentChars);
    }

    [Fact]
    public void Alert_body_reserves_the_stop_notice_even_when_the_body_is_long()
    {
        var longBody = new string('x', 500);

        var rendered = SmsNotificationDisclosure.RenderAlertBody(longBody, includeStopNotice: true);

        // The disclosure is preserved in full; the VARIABLE body is truncated to fit one segment.
        rendered.Should().StartWith(SmsNotificationDisclosure.SenderIdentity);
        rendered.Should().EndWith(SmsNotificationDisclosure.StopNotice);
        rendered.Should().Contain("...", "the body is truncated, never the disclosure");
        // The truncation marker must stay GSM-7 (an ASCII "...", not a "…" U+2026) so the message stays a single
        // billable segment rather than tipping the whole body into UCS-2 (docs/36 §7).
        rendered.Should().NotContain("…");
        rendered.Length.Should().BeLessThanOrEqualTo(SmsNotificationDisclosure.SingleSegmentChars);
    }

    [Fact]
    public void Short_alert_body_is_not_truncated_but_still_disclosed()
    {
        var rendered = SmsNotificationDisclosure.RenderAlertBody("Break-glass sign-in detected", includeStopNotice: true);

        rendered.Should().Contain("Break-glass sign-in detected");
        rendered.Should().EndWith(SmsNotificationDisclosure.StopNotice);
        rendered.Should().NotContain("…");
    }

    [Fact]
    public void Disclosure_text_and_version_are_present_for_the_consent_event()
    {
        SmsNotificationDisclosure.DisclosureText.Should().Contain("STOP");
        SmsNotificationDisclosure.DisclosureText.Should().Contain("Msg & data rates");
        SmsNotificationDisclosure.DisclosureVersion.Should().NotBeNullOrWhiteSpace();
    }
}
