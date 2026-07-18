using FluentAssertions;
using WireHQ.Domain.Notifications;
using Xunit;

namespace WireHQ.Domain.UnitTests.Notifications;

public sealed class NotificationRuleTests
{
    private static readonly Guid Org = Guid.CreateVersion7();

    [Fact]
    public void Create_succeeds_for_a_valid_email_rule()
    {
        var result = NotificationRule.Create(Org, "MFA alerts", "mfa.*", ChannelKind.Email, NotificationAudience.OptedInUsers, null, requiredFeatures: []);

        result.IsSuccess.Should().BeTrue();
        result.Value.Enabled.Should().BeTrue();
        result.Value.OrganizationId.Should().Be(Org);
        result.Value.IsAdvanced.Should().BeFalse("a single-pattern rule is not advanced");
        result.Value.RequiredFeatures.Should().BeEmpty("a free-core Email rule holds no feature keys");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_rejects_a_blank_name(string name)
    {
        NotificationRule.Create(Org, name, "mfa.*", ChannelKind.Email, NotificationAudience.OptedInUsers, null, [])
            .Error.Should().Be(NotificationErrors.InvalidName);
    }

    [Fact]
    public void Create_rejects_a_blank_pattern()
    {
        NotificationRule.Create(Org, "rule", " ", ChannelKind.Email, NotificationAudience.OptedInUsers, null, [])
            .Error.Should().Be(NotificationErrors.InvalidPattern);
    }

    [Fact]
    public void Create_requires_a_role_id_for_a_role_audience()
    {
        NotificationRule.Create(Org, "rule", "mfa.*", ChannelKind.Email, NotificationAudience.Role, audienceRef: null, requiredFeatures: [])
            .Error.Should().Be(NotificationErrors.MissingRole);
    }

    [Theory]
    [InlineData("mfa.*", "mfa.enrolled", true)]
    [InlineData("mfa.*", "mfa", true)]
    [InlineData("mfa.*", "identity.users.created", false)]
    [InlineData("identity.users.created", "identity.users.created", true)]
    [InlineData("*", "anything.at.all", true)]
    public void Matches_honours_the_glob(string pattern, string action, bool expected)
    {
        var rule = NotificationRule.Create(Org, "rule", pattern, ChannelKind.Email, NotificationAudience.OptedInUsers, null, []).Value;
        rule.Matches(action).Should().Be(expected);
    }

    [Fact]
    public void A_disabled_rule_matches_nothing()
    {
        var rule = NotificationRule.Create(Org, "rule", "*", ChannelKind.Email, NotificationAudience.OptedInUsers, null, []).Value;
        rule.Disable();
        rule.Matches("mfa.enrolled").Should().BeFalse();
    }

    [Fact]
    public void A_multi_pattern_rule_fires_on_the_primary_and_any_additional_glob()
    {
        var rule = NotificationRule.Create(
            Org, "security", "mfa.*", ChannelKind.Email, NotificationAudience.OptedInUsers, null,
            requiredFeatures: ["notifications.routing"],
            additionalPatterns: ["identity.users.*", "policy.access.*"]).Value;

        rule.IsAdvanced.Should().BeTrue("it carries additional patterns");
        rule.AllPatterns.Should().BeEquivalentTo(["mfa.*", "identity.users.*", "policy.access.*"]);

        rule.Matches("mfa.enrolled").Should().BeTrue("the primary glob matches");
        rule.Matches("identity.users.created").Should().BeTrue("an additional glob matches");
        rule.Matches("policy.access.updated").Should().BeTrue("another additional glob matches");
        rule.Matches("webhooks.created").Should().BeFalse("no pattern matches this action");
    }

    [Fact]
    public void Additional_patterns_equal_to_the_primary_or_blank_are_dropped()
    {
        var rule = NotificationRule.Create(
            Org, "rule", "mfa.*", ChannelKind.Email, NotificationAudience.OptedInUsers, null,
            requiredFeatures: [],
            additionalPatterns: ["mfa.*", "  ", "mfa.*"]).Value;

        rule.IsAdvanced.Should().BeFalse("every extra collapsed to the primary or blank — the rule stays single-pattern");
        rule.AdditionalPatterns.Should().BeEmpty();
    }

    [Fact]
    public void Too_many_additional_patterns_are_rejected()
    {
        var extras = Enumerable.Range(0, NotificationRule.MaxAdditionalPatterns + 1).Select(i => $"evt.{i}").ToArray();

        NotificationRule.Create(
            Org, "rule", "mfa.*", ChannelKind.Email, NotificationAudience.OptedInUsers, null,
            requiredFeatures: ["notifications.routing"], additionalPatterns: extras)
            .Error.Should().Be(NotificationErrors.TooManyPatterns);
    }

    [Theory]
    [InlineData(DigestCadence.Daily)]
    [InlineData(DigestCadence.Weekly)]
    public void A_non_immediate_digest_cadence_is_advanced(DigestCadence cadence)
    {
        var rule = NotificationRule.Create(
            Org, "digest", "mfa.*", ChannelKind.Email, NotificationAudience.OptedInUsers, null,
            requiredFeatures: ["notifications.routing"], additionalPatterns: null,
            digestCadence: cadence, nextDigestAtUtc: DateTimeOffset.UnixEpoch).Value;

        rule.IsAdvanced.Should().BeTrue("a digest is an advanced shape");
        rule.DigestCadence.Should().Be(cadence);
        rule.NextDigestAtUtc.Should().Be(DateTimeOffset.UnixEpoch);
    }

    [Fact]
    public void An_immediate_single_pattern_rule_is_not_advanced_and_has_no_cursor()
    {
        var rule = NotificationRule.Create(Org, "r", "mfa.*", ChannelKind.Email, NotificationAudience.OptedInUsers, null, requiredFeatures: []).Value;

        rule.IsAdvanced.Should().BeFalse();
        rule.DigestCadence.Should().Be(DigestCadence.Immediate);
        rule.NextDigestAtUtc.Should().BeNull();
    }

    [Fact]
    public void AdvanceDigestCursor_moves_the_cursor()
    {
        var rule = NotificationRule.Create(
            Org, "r", "mfa.*", ChannelKind.Email, NotificationAudience.OptedInUsers, null,
            requiredFeatures: ["notifications.routing"], additionalPatterns: null,
            digestCadence: DigestCadence.Daily, nextDigestAtUtc: DateTimeOffset.UnixEpoch).Value;

        var next = DateTimeOffset.UnixEpoch.AddDays(1);
        rule.AdvanceDigestCursor(next);

        rule.NextDigestAtUtc.Should().Be(next);
    }

    [Fact]
    public void Update_replaces_the_additional_patterns()
    {
        var rule = NotificationRule.Create(
            Org, "rule", "mfa.*", ChannelKind.Email, NotificationAudience.OptedInUsers, null,
            requiredFeatures: ["notifications.routing"], additionalPatterns: ["identity.users.*"]).Value;

        rule.Update("rule", "mfa.*", ChannelKind.Email, NotificationAudience.OptedInUsers, null,
            requiredFeatures: ["notifications.routing"], additionalPatterns: ["policy.access.*", "webhooks.*"]);

        rule.AdditionalPatterns.Select(p => p.Pattern).Should().BeEquivalentTo(["policy.access.*", "webhooks.*"]);
        rule.Matches("identity.users.created").Should().BeFalse("the old additional pattern was removed");
        rule.Matches("webhooks.created").Should().BeTrue("the new additional pattern applies");
    }

    [Fact]
    public void A_quiet_hours_window_is_advanced_and_stored()
    {
        var rule = NotificationRule.Create(
            Org, "quiet", "mfa.*", ChannelKind.Email, NotificationAudience.OptedInUsers, null,
            requiredFeatures: ["notifications.routing"],
            quietHoursStart: new TimeOnly(22, 0), quietHoursEnd: new TimeOnly(7, 0), quietHoursTimeZone: "America/New_York").Value;

        rule.IsAdvanced.Should().BeTrue("a quiet-hours window is an advanced shape");
        rule.QuietHoursEnabled.Should().BeTrue();
        rule.QuietHoursStart.Should().Be(new TimeOnly(22, 0));
        rule.QuietHoursEnd.Should().Be(new TimeOnly(7, 0));
        rule.QuietHoursTimeZone.Should().Be("America/New_York");
    }

    [Theory]
    // Partial windows, a zero-length window, and an unresolvable time zone are all rejected identically.
    [InlineData("22:00", null, "America/New_York")]
    [InlineData(null, "07:00", "America/New_York")]
    [InlineData("22:00", "07:00", null)]
    [InlineData("22:00", "22:00", "America/New_York")]  // zero-length window
    [InlineData("22:00", "07:00", "Not/ARealZone")]     // unresolvable time zone
    public void An_incomplete_or_invalid_quiet_window_is_rejected(string? start, string? end, string? tz)
    {
        var result = NotificationRule.Create(
            Org, "quiet", "mfa.*", ChannelKind.Email, NotificationAudience.OptedInUsers, null,
            requiredFeatures: ["notifications.routing"],
            quietHoursStart: start is null ? (TimeOnly?)null : TimeOnly.Parse(start),
            quietHoursEnd: end is null ? (TimeOnly?)null : TimeOnly.Parse(end),
            quietHoursTimeZone: tz);

        result.Error.Should().Be(NotificationErrors.InvalidQuietHours);
    }

    [Fact]
    public void Update_can_clear_quiet_hours()
    {
        var rule = NotificationRule.Create(
            Org, "quiet", "mfa.*", ChannelKind.Email, NotificationAudience.OptedInUsers, null,
            requiredFeatures: ["notifications.routing"],
            quietHoursStart: new TimeOnly(22, 0), quietHoursEnd: new TimeOnly(7, 0), quietHoursTimeZone: "Etc/UTC").Value;
        rule.QuietHoursEnabled.Should().BeTrue();

        rule.Update("quiet", "mfa.*", ChannelKind.Email, NotificationAudience.OptedInUsers, null, requiredFeatures: []);

        rule.QuietHoursEnabled.Should().BeFalse("passing no window clears it");
        rule.QuietHoursStart.Should().BeNull();
        rule.QuietHoursTimeZone.Should().BeNull();
    }

    [Fact]
    public void An_escalation_chain_is_advanced_and_ordered()
    {
        var rule = NotificationRule.Create(
            Org, "on-call", "mfa.*", ChannelKind.Email, NotificationAudience.OptedInUsers, null,
            requiredFeatures: ["notifications.routing"],
            escalationSteps:
            [
                new EscalationStepSpec(5, ChannelKind.Email, NotificationAudience.Role, Guid.CreateVersion7()),
                new EscalationStepSpec(15, ChannelKind.Email, NotificationAudience.OptedInUsers, null),
            ]).Value;

        rule.IsAdvanced.Should().BeTrue("an escalation chain is an advanced shape");
        rule.HasEscalation.Should().BeTrue();
        rule.EscalationSteps.Select(s => s.StepOrder).Should().Equal(0, 1);
        rule.EscalationSteps.Should().OnlyContain(s => s.RuleId == rule.Id);
    }

    [Fact]
    public void Escalation_is_rejected_with_a_digest_cadence()
    {
        NotificationRule.Create(
            Org, "r", "mfa.*", ChannelKind.Email, NotificationAudience.OptedInUsers, null,
            requiredFeatures: ["notifications.routing"], additionalPatterns: null,
            digestCadence: DigestCadence.Daily, nextDigestAtUtc: DateTimeOffset.UnixEpoch,
            escalationSteps: [new EscalationStepSpec(5, ChannelKind.Email, NotificationAudience.OptedInUsers, null)])
            .Error.Should().Be(NotificationErrors.EscalationRequiresImmediate);
    }

    [Fact]
    public void Too_many_escalation_steps_are_rejected()
    {
        var steps = Enumerable.Range(0, NotificationRule.MaxEscalationSteps + 1)
            .Select(_ => new EscalationStepSpec(5, ChannelKind.Email, NotificationAudience.OptedInUsers, null)).ToArray();

        NotificationRule.Create(Org, "r", "mfa.*", ChannelKind.Email, NotificationAudience.OptedInUsers, null,
            requiredFeatures: ["notifications.routing"], escalationSteps: steps)
            .Error.Should().Be(NotificationErrors.TooManyEscalationSteps);
    }

    [Theory]
    [InlineData(0)]      // below the minimum delay
    [InlineData(20000)]  // above the maximum delay
    public void An_escalation_step_with_a_bad_delay_is_rejected(int delay)
    {
        NotificationRule.Create(Org, "r", "mfa.*", ChannelKind.Email, NotificationAudience.OptedInUsers, null,
            requiredFeatures: ["notifications.routing"],
            escalationSteps: [new EscalationStepSpec(delay, ChannelKind.Email, NotificationAudience.OptedInUsers, null)])
            .Error.Should().Be(NotificationErrors.InvalidEscalationStep);
    }

    [Fact]
    public void An_escalation_step_targeting_a_role_needs_a_role_id()
    {
        NotificationRule.Create(Org, "r", "mfa.*", ChannelKind.Email, NotificationAudience.OptedInUsers, null,
            requiredFeatures: ["notifications.routing"],
            escalationSteps: [new EscalationStepSpec(5, ChannelKind.Email, NotificationAudience.Role, AudienceRef: null)])
            .Error.Should().Be(NotificationErrors.InvalidEscalationStep);
    }

    [Fact]
    public void Update_can_clear_the_escalation_chain()
    {
        var rule = NotificationRule.Create(Org, "r", "mfa.*", ChannelKind.Email, NotificationAudience.OptedInUsers, null,
            requiredFeatures: ["notifications.routing"],
            escalationSteps: [new EscalationStepSpec(5, ChannelKind.Email, NotificationAudience.OptedInUsers, null)]).Value;
        rule.HasEscalation.Should().BeTrue();

        rule.Update("r", "mfa.*", ChannelKind.Email, NotificationAudience.OptedInUsers, null, requiredFeatures: []);

        rule.HasEscalation.Should().BeFalse("passing no steps clears the chain");
        rule.EscalationSteps.Should().BeEmpty();
    }
}
