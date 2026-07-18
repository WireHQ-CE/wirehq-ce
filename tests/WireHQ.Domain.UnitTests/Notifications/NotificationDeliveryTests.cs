using FluentAssertions;
using WireHQ.Domain.Notifications;
using Xunit;

namespace WireHQ.Domain.UnitTests.Notifications;

public sealed class NotificationDeliveryTests
{
    private static NotificationDelivery NewDelivery(DateTimeOffset now) =>
        NotificationDelivery.Create(
            Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), ChannelKind.Email, requiredFeatures: [],
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

    [Fact]
    public void Defer_moves_the_due_time_without_counting_an_attempt()
    {
        var now = DateTimeOffset.UnixEpoch;
        var delivery = NewDelivery(now);
        var createdAt = delivery.CreatedAtUtc;

        var until = now + TimeSpan.FromHours(8);
        delivery.Defer(until);

        delivery.NextAttemptAtUtc.Should().Be(until);
        delivery.Status.Should().Be(NotificationDeliveryStatus.Pending); // a deferral is not a send…
        delivery.Attempts.Should().Be(0);                                // …nor a failed attempt
        delivery.CreatedAtUtc.Should().Be(createdAt);                    // creation time is never rewritten
        delivery.IsTerminal.Should().BeFalse();
    }

    [Fact]
    public void Defer_is_a_no_op_once_terminal()
    {
        var now = DateTimeOffset.UnixEpoch;
        var delivery = NewDelivery(now);
        delivery.Cancel("module inactive");

        delivery.Defer(now + TimeSpan.FromHours(8));

        delivery.Status.Should().Be(NotificationDeliveryStatus.Cancelled); // a terminal row is never resurrected
        delivery.NextAttemptAtUtc.Should().BeNull();
    }

    [Fact]
    public void QuietDeferUntil_uses_the_copied_window_and_is_null_without_one()
    {
        // 22:00–07:00 UTC; a delivery whose window is active now defers to the next window end.
        var now = DateTimeOffset.Parse("2026-07-16T23:00:00Z");
        var quiet = NotificationDelivery.Create(
            Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), ChannelKind.Email, requiredFeatures: [],
            "user@example.com", "subject", "body", dedupValue: null, now,
            new TimeOnly(22, 0), new TimeOnly(7, 0), "Etc/UTC");
        quiet.QuietDeferUntil(now).Should().Be(DateTimeOffset.Parse("2026-07-17T07:00:00Z"));

        // A delivery with no window copied (a free-core rule, or a test send) is never deferred.
        NewDelivery(now).QuietDeferUntil(now).Should().BeNull();
    }
}
