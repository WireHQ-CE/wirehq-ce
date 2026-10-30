using FluentAssertions;
using WireHQ.Domain.Notifications;
using Xunit;

namespace WireHQ.Domain.UnitTests.Notifications;

/// <summary>SMS critical-only gating (docs/36 §6, Wave 4 Slice C): an SMS rule — whether SMS is the primary channel or
/// an escalation step — may only target literal critical actions, must be immediate, and can't be held during quiet
/// hours. Enforced in the shared <c>Validate</c> so Create AND Update behave identically.</summary>
public sealed class NotificationRuleSmsCriticalTests
{
    private static readonly Guid Org = Guid.CreateVersion7();
    private static readonly string Critical = NotificationCriticalEvents.All[0].Action; // "mfa.disabled"
    private static readonly string Critical2 = NotificationCriticalEvents.All[1].Action; // an sso action

    [Fact]
    public void Create_succeeds_for_an_sms_rule_on_a_literal_critical_event()
    {
        var result = NotificationRule.Create(
            Org, "Break-glass alerts", Critical, ChannelKind.Sms, NotificationAudience.OptedInUsers, null,
            requiredFeatures: ["notifications.sms"]);

        result.IsSuccess.Should().BeTrue();
        result.Value.ChannelKind.Should().Be(ChannelKind.Sms);
    }

    [Fact]
    public void Create_allows_multiple_literal_critical_patterns_on_an_sms_rule()
    {
        // The server permits any SET of literal critical actions (the editor restricts to one for UX simplicity).
        var result = NotificationRule.Create(
            Org, "Security SMS", Critical, ChannelKind.Sms, NotificationAudience.OptedInUsers, null,
            requiredFeatures: ["notifications.sms", "notifications.routing"], additionalPatterns: [Critical2]);

        result.IsSuccess.Should().BeTrue();
    }

    [Theory]
    [InlineData("*")]
    [InlineData("mfa.*")]
    [InlineData("policy.access.*")]
    public void Create_rejects_a_glob_pattern_on_an_sms_rule(string glob)
    {
        NotificationRule.Create(
            Org, "SMS", glob, ChannelKind.Sms, NotificationAudience.OptedInUsers, null,
            requiredFeatures: ["notifications.sms"])
            .Error.Should().Be(NotificationErrors.SmsCriticalOnly);
    }

    [Theory]
    [InlineData("identity.users.invited")] // a real-looking action, but not on the curated critical list
    [InlineData("mfa.enabled")]            // adjacent to a critical action but not itself critical
    public void Create_rejects_a_non_critical_literal_on_an_sms_rule(string action)
    {
        NotificationRule.Create(
            Org, "SMS", action, ChannelKind.Sms, NotificationAudience.OptedInUsers, null,
            requiredFeatures: ["notifications.sms"])
            .Error.Should().Be(NotificationErrors.SmsCriticalOnly);
    }

    [Fact]
    public void Create_rejects_a_non_critical_additional_pattern_on_an_sms_rule()
    {
        // Primary is critical, but a broad additional glob would fan SMS onto everything at runtime — rejected.
        NotificationRule.Create(
            Org, "SMS", Critical, ChannelKind.Sms, NotificationAudience.OptedInUsers, null,
            requiredFeatures: ["notifications.sms", "notifications.routing"], additionalPatterns: ["mfa.*"])
            .Error.Should().Be(NotificationErrors.SmsCriticalOnly);
    }

    [Theory]
    [InlineData(DigestCadence.Daily)]
    [InlineData(DigestCadence.Weekly)]
    public void Create_rejects_a_digest_on_an_sms_rule(DigestCadence cadence)
    {
        NotificationRule.Create(
            Org, "SMS", Critical, ChannelKind.Sms, NotificationAudience.OptedInUsers, null,
            requiredFeatures: ["notifications.sms", "notifications.routing"], additionalPatterns: null,
            digestCadence: cadence, nextDigestAtUtc: DateTimeOffset.UnixEpoch)
            .Error.Should().Be(NotificationErrors.SmsImmediateOnly);
    }

    [Fact]
    public void Create_rejects_quiet_hours_on_an_sms_rule()
    {
        NotificationRule.Create(
            Org, "SMS", Critical, ChannelKind.Sms, NotificationAudience.OptedInUsers, null,
            requiredFeatures: ["notifications.sms", "notifications.routing"],
            quietHoursStart: new TimeOnly(22, 0), quietHoursEnd: new TimeOnly(7, 0), quietHoursTimeZone: "Etc/UTC")
            .Error.Should().Be(NotificationErrors.SmsNoQuietHours);
    }

    [Fact]
    public void Create_rejects_a_glob_when_an_escalation_step_is_sms()
    {
        // An Email-primary rule with an SMS escalation step still triggers the critical-only check (the "primary OR any
        // step is Sms" rule) — so a broad primary glob is rejected even though the primary channel is Email. (The
        // command layer additionally hard-rejects SMS escalation steps until Slice F; this is the domain backstop.)
        NotificationRule.Create(
            Org, "Escalate to SMS", "mfa.*", ChannelKind.Email, NotificationAudience.OptedInUsers, null,
            requiredFeatures: ["notifications.routing"],
            escalationSteps: [new EscalationStepSpec(5, ChannelKind.Sms, NotificationAudience.OptedInUsers, null)])
            .Error.Should().Be(NotificationErrors.SmsCriticalOnly);
    }

    [Fact]
    public void An_email_rule_with_a_glob_is_unaffected_by_sms_gating()
    {
        NotificationRule.Create(
            Org, "Email digest", "mfa.*", ChannelKind.Email, NotificationAudience.OptedInUsers, null,
            requiredFeatures: [])
            .IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void Update_enforces_sms_critical_only_when_switching_an_email_rule_to_sms()
    {
        var rule = NotificationRule.Create(
            Org, "rule", "mfa.*", ChannelKind.Email, NotificationAudience.OptedInUsers, null, requiredFeatures: []).Value;

        // Switching to SMS while keeping the glob pattern must be rejected on the Update path too (no smuggling).
        rule.Update("rule", "mfa.*", ChannelKind.Sms, NotificationAudience.OptedInUsers, null,
                requiredFeatures: ["notifications.sms"])
            .Error.Should().Be(NotificationErrors.SmsCriticalOnly);

        // The same switch onto a literal critical event is accepted.
        rule.Update("rule", Critical, ChannelKind.Sms, NotificationAudience.OptedInUsers, null,
                requiredFeatures: ["notifications.sms"])
            .IsSuccess.Should().BeTrue();
        rule.ChannelKind.Should().Be(ChannelKind.Sms);
        rule.EventPattern.Should().Be(Critical);
    }
}
