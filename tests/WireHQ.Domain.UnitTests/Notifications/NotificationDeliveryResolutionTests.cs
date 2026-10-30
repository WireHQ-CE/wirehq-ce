using FluentAssertions;
using WireHQ.Domain.Notifications;
using Xunit;

namespace WireHQ.Domain.UnitTests.Notifications;

/// <summary>
/// The <c>Unresolved</c> state machine (docs/36 §7/§12, SMS Wave 4 W4). A send whose outcome we never learned is
/// terminal and never retried; the provider's status callback may later resolve it — and only it.
/// </summary>
public sealed class NotificationDeliveryResolutionTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 18, 9, 0, 0, TimeSpan.Zero);

    private static NotificationDelivery NewSms() =>
        NotificationDelivery.Create(
            organizationId: Guid.CreateVersion7(), ruleId: Guid.CreateVersion7(), jobId: Guid.CreateVersion7(),
            ChannelKind.Sms, requiredFeatures: new[] { "notifications.sms" }, recipient: "+447700900123",
            subject: "WireHQ: alert", body: "MFA was disabled", dedupValue: null, nowUtc: Now);

    [Fact]
    public void MarkUnresolved_is_terminal_and_schedules_nothing()
    {
        var delivery = NewSms();

        delivery.MarkUnresolved("The provider did not confirm.", "SM123");

        delivery.Status.Should().Be(NotificationDeliveryStatus.Unresolved);
        delivery.IsTerminal.Should().BeTrue("otherwise the drain re-selects it on every tick, forever");
        delivery.NextAttemptAtUtc.Should().BeNull();
        delivery.Attempts.Should().Be(1, "an attempt genuinely happened — it is the outcome we do not know");
        delivery.ProviderMessageId.Should().Be("SM123");
    }

    [Fact]
    public void Unresolved_is_a_distinct_state_from_Cancelled()
    {
        // Not cosmetic. "Cancelled" asserts we did not send, which is the opposite of what we know: we may well have.
        // A ledger that misstates its own facts is the defect — the same reasoning that split ClearedKeyword out of
        // Keyword on the opt-out ledger.
        var delivery = NewSms();

        delivery.MarkUnresolved("The provider did not confirm.");

        delivery.Status.Should().NotBe(NotificationDeliveryStatus.Cancelled);
        delivery.Status.Should().NotBe(NotificationDeliveryStatus.Failed);
        delivery.Status.Should().NotBe(NotificationDeliveryStatus.Delivered);
    }

    [Fact]
    public void A_status_callback_resolves_an_unresolved_delivery_to_delivered()
    {
        var delivery = NewSms();
        delivery.MarkUnresolved("The provider did not confirm.");

        var changed = delivery.ResolveFromProvider(delivered: true, "SM999", reason: null, Now.AddMinutes(2));

        changed.Should().BeTrue();
        delivery.Status.Should().Be(NotificationDeliveryStatus.Delivered);
        delivery.DeliveredAtUtc.Should().Be(Now.AddMinutes(2));
        delivery.LastError.Should().BeNull();
        delivery.ProviderMessageId.Should().Be("SM999");
    }

    [Fact]
    public void A_failure_callback_resolves_to_failed_without_re_entering_the_retry_ladder()
    {
        var delivery = NewSms();
        delivery.MarkUnresolved("The provider did not confirm.");

        delivery.ResolveFromProvider(delivered: false, "SM999", "The provider reported delivery failure (code 30006).", Now);

        delivery.Status.Should().Be(NotificationDeliveryStatus.Failed);
        delivery.NextAttemptAtUtc.Should().BeNull("the operator decision is that an unresolved send is terminal");
        delivery.DeliveredAtUtc.Should().BeNull();
    }

    [Fact]
    public void A_replayed_callback_is_a_no_op()
    {
        // Providers retry status callbacks. Applying the same one twice must not move a resolved row again.
        var delivery = NewSms();
        delivery.MarkUnresolved("The provider did not confirm.");
        delivery.ResolveFromProvider(delivered: true, "SM999", null, Now);

        var second = delivery.ResolveFromProvider(delivered: false, "SM999", "late failure", Now.AddHours(1));

        second.Should().BeFalse();
        delivery.Status.Should().Be(NotificationDeliveryStatus.Delivered, "the first resolution stands");
    }

    [Fact]
    public void A_callback_can_never_rewrite_an_outcome_we_observed_ourselves()
    {
        // A delivery we saw succeed, or saw cancelled for an opt-out, is not up for revision by a late webhook.
        var delivered = NewSms();
        delivered.MarkSucceeded(201, Now, "SM111");
        delivered.ResolveFromProvider(delivered: false, "SM111", "late failure", Now.AddHours(1)).Should().BeFalse();
        delivered.Status.Should().Be(NotificationDeliveryStatus.Delivered);

        var cancelled = NewSms();
        cancelled.Cancel("Recipient has opted out of SMS");
        cancelled.ResolveFromProvider(delivered: true, "SM222", null, Now).Should().BeFalse();
        cancelled.Status.Should().Be(NotificationDeliveryStatus.Cancelled);
    }

    [Fact]
    public void The_first_provider_message_id_wins()
    {
        // The SID we were given at send time is the one that names the message. A callback claiming a different id is
        // either a different message or a mistake; either way it must not overwrite the operator's reference.
        var delivery = NewSms();
        delivery.MarkUnresolved("no confirmation", "SM-original");

        delivery.ResolveFromProvider(delivered: true, "SM-different", null, Now);

        delivery.ProviderMessageId.Should().Be("SM-original");
    }
}
