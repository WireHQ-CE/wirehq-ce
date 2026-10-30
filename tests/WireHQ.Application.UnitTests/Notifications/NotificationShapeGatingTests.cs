using FluentAssertions;
using NSubstitute;
using WireHQ.Application.Entitlements;
using WireHQ.Application.Features.Notifications;
using WireHQ.Domain.Notifications;
using Xunit;

namespace WireHQ.Application.UnitTests.Notifications;

/// <summary>
/// SMS shape-gating (docs/36 §10, Slice A): a <b>primary</b>-SMS rule is recognised and gated on <c>notifications.sms</c>,
/// while an <b>SMS escalation step</b> stays hard-rejected (escalation over SMS is deferred to Slice F). Exercised through
/// the internal <see cref="NotificationShapeGating"/> helper (visible to the tests via InternalsVisibleTo).
/// </summary>
public sealed class NotificationShapeGatingTests
{
    private readonly IEntitlementService _entitlements = Substitute.For<IEntitlementService>();

    [Fact]
    public async Task Primary_sms_rule_is_accepted_and_requires_notifications_sms_when_the_org_is_entitled()
    {
        _entitlements.HasFeatureAsync(PlanFeatures.NotificationsSms, Arg.Any<CancellationToken>()).Returns(true);

        var result = await NotificationShapeGating.ResolveAsync(
            ChannelKind.Sms, isAdvanced: false, _entitlements, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Contain(PlanFeatures.NotificationsSms);
    }

    [Fact]
    public async Task Primary_sms_rule_is_rejected_when_the_org_lacks_notifications_sms()
    {
        _entitlements.HasFeatureAsync(PlanFeatures.NotificationsSms, Arg.Any<CancellationToken>()).Returns(false);

        var result = await NotificationShapeGating.ResolveAsync(
            ChannelKind.Sms, isAdvanced: false, _entitlements, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(NotificationErrors.ChannelNotAvailable);
    }

    [Fact]
    public async Task An_sms_escalation_step_is_hard_rejected_even_when_the_org_holds_notifications_sms()
    {
        // The SMS channel now exists, but SMS ESCALATION carries idempotency + multi-segment-spend risk and is deferred
        // to Slice F — the author-time reject must stay (docs/36 §2/§10).
        _entitlements.HasFeatureAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);
        var steps = new[] { new EscalationStepSpec(DelayMinutes: 5, ChannelKind.Sms, NotificationAudience.OptedInUsers, AudienceRef: null) };

        var error = await NotificationShapeGating.ValidateEscalationChannelsAsync(steps, _entitlements, CancellationToken.None);

        error.Should().Be(NotificationErrors.EscalationChannelUnavailable);
    }
}
