using FluentAssertions;
using WireHQ.Domain.Notifications;
using Xunit;

namespace WireHQ.Domain.UnitTests.Notifications;

public sealed class NotificationDeliveryTests
{
    private static NotificationDelivery NewDelivery(DateTimeOffset now) =>
        NotificationDelivery.Create(
            Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), ChannelKind.Email, requiredFeature: null,
            "user@example.com", "subject", "body", dedupValue: null, now);

    [Fact]
    public void A_new_delivery_is_pending_and_due_now()
    {
        var now = DateTimeOffset.UnixEpoch;
        var delivery = NewDelivery(now);

        delivery.Status.Should().Be(NotificationDeliveryStatus.Pending);
        delivery.Attempts.Should().Be(0);
        delivery.NextAttemptAtUtc.Should().Be(now);
        delivery.IsTerminal.Should().BeFalse();
    }

    [Fact]
    public void MarkSucceeded_is_terminal()
    {
        var now = DateTimeOffset.UnixEpoch;
        var delivery = NewDelivery(now);

        delivery.MarkSucceeded(200, now);

        delivery.Status.Should().Be(NotificationDeliveryStatus.Delivered);
        delivery.NextAttemptAtUtc.Should().BeNull();
        delivery.DeliveredAtUtc.Should().Be(now);
        delivery.IsTerminal.Should().BeTrue();
    }

    [Fact]
    public void MarkFailed_reschedules_with_backoff_then_fails_after_the_cap()
    {
        var now = DateTimeOffset.UnixEpoch;
        var delivery = NewDelivery(now);

        // First failure → retry at +30s.
        delivery.MarkFailed(500, "boom", now);
        delivery.Status.Should().Be(NotificationDeliveryStatus.Pending);
        delivery.Attempts.Should().Be(1);
        delivery.NextAttemptAtUtc.Should().Be(now + TimeSpan.FromSeconds(30));

        // Exhaust the remaining attempts → terminal Failed on the 6th.
        for (var i = 0; i < NotificationDelivery.MaxAttempts - 1; i++)
        {
            delivery.MarkFailed(500, "boom", now);
        }

        delivery.Attempts.Should().Be(NotificationDelivery.MaxAttempts);
        delivery.Status.Should().Be(NotificationDeliveryStatus.Failed);
        delivery.NextAttemptAtUtc.Should().BeNull();
        delivery.IsTerminal.Should().BeTrue();
    }

    [Fact]
    public void Cancel_is_a_distinct_terminal_outcome()
    {
        var delivery = NewDelivery(DateTimeOffset.UnixEpoch);

        delivery.Cancel("module inactive");

        delivery.Status.Should().Be(NotificationDeliveryStatus.Cancelled);
        delivery.NextAttemptAtUtc.Should().BeNull();
        delivery.IsTerminal.Should().BeTrue();
    }
}
