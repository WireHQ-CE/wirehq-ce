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
        var result = NotificationRule.Create(Org, "MFA alerts", "mfa.*", ChannelKind.Email, NotificationAudience.OptedInUsers, null, requiredFeature: null);

        result.IsSuccess.Should().BeTrue();
        result.Value.Enabled.Should().BeTrue();
        result.Value.OrganizationId.Should().Be(Org);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_rejects_a_blank_name(string name)
    {
        NotificationRule.Create(Org, name, "mfa.*", ChannelKind.Email, NotificationAudience.OptedInUsers, null, null)
            .Error.Should().Be(NotificationErrors.InvalidName);
    }

    [Fact]
    public void Create_rejects_a_blank_pattern()
    {
        NotificationRule.Create(Org, "rule", " ", ChannelKind.Email, NotificationAudience.OptedInUsers, null, null)
            .Error.Should().Be(NotificationErrors.InvalidPattern);
    }

    [Fact]
    public void Create_requires_a_role_id_for_a_role_audience()
    {
        NotificationRule.Create(Org, "rule", "mfa.*", ChannelKind.Email, NotificationAudience.Role, audienceRef: null, requiredFeature: null)
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
        var rule = NotificationRule.Create(Org, "rule", pattern, ChannelKind.Email, NotificationAudience.OptedInUsers, null, null).Value;
        rule.Matches(action).Should().Be(expected);
    }

    [Fact]
    public void A_disabled_rule_matches_nothing()
    {
        var rule = NotificationRule.Create(Org, "rule", "*", ChannelKind.Email, NotificationAudience.OptedInUsers, null, null).Value;
        rule.Disable();
        rule.Matches("mfa.enrolled").Should().BeFalse();
    }
}
