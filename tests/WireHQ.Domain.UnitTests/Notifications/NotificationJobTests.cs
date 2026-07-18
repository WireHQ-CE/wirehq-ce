using FluentAssertions;
using WireHQ.Domain.Notifications;
using Xunit;

namespace WireHQ.Domain.UnitTests.Notifications;

/// <summary>
/// The job's escalation state machine (docs/35 §5, Wave 3): a rule with an escalation chain keeps its job LIVE
/// (Escalating) with a time cursor; the drain fires the next step when due, and the chain stops on acknowledge,
/// exhaustion, or module deactivation — all of which return the job to <see cref="NotificationJobStatus.Expanded"/>.
/// </summary>
public sealed class NotificationJobTests
{
    private static NotificationJob NewJob() =>
        NotificationJob.Create(
            Guid.CreateVersion7(), Guid.CreateVersion7(), auditLogId: null, "mfa.enrolled", "MFA enrolled",
            DigestCadence.Immediate, DateTimeOffset.UnixEpoch);

    [Fact]
    public void BeginEscalating_arms_the_first_cursor()
    {
        var job = NewJob();
        var due = DateTimeOffset.UnixEpoch.AddMinutes(5);

        job.BeginEscalating(stepCount: 2, firstStepDueUtc: due);

        job.IsEscalating.Should().BeTrue();
        job.EscalationLevel.Should().Be(0);
        job.EscalationStepCount.Should().Be(2);
        job.EscalationNextDueAtUtc.Should().Be(due);
    }

    [Fact]
    public void AdvanceEscalation_moves_the_level_then_settles_when_exhausted()
    {
        var job = NewJob();
        job.BeginEscalating(2, DateTimeOffset.UnixEpoch.AddMinutes(5));

        // Fired step 0 → level 1, cursor set to when step 1 comes due.
        var next = DateTimeOffset.UnixEpoch.AddMinutes(15);
        job.AdvanceEscalation(next);
        job.EscalationLevel.Should().Be(1);
        job.EscalationNextDueAtUtc.Should().Be(next);
        job.IsEscalating.Should().BeTrue();

        // Fired step 1 (the last) → null cursor → chain exhausted → settles to Expanded.
        job.AdvanceEscalation(null);
        job.EscalationLevel.Should().Be(2);
        job.EscalationNextDueAtUtc.Should().BeNull();
        job.IsEscalating.Should().BeFalse();
        job.Status.Should().Be(NotificationJobStatus.Expanded);
    }

    [Fact]
    public void Acknowledge_records_who_and_stops_the_chain()
    {
        var job = NewJob();
        job.BeginEscalating(3, DateTimeOffset.UnixEpoch.AddMinutes(5));
        var user = Guid.CreateVersion7();

        job.Acknowledge(user, DateTimeOffset.UnixEpoch.AddMinutes(2));

        job.AcknowledgedBy.Should().Be(user);
        job.AcknowledgedAtUtc.Should().Be(DateTimeOffset.UnixEpoch.AddMinutes(2));
        job.EscalationNextDueAtUtc.Should().BeNull();
        job.IsEscalating.Should().BeFalse("an acknowledged job is no longer an escalation candidate");
    }

    [Fact]
    public void SettleEscalation_stops_without_recording_an_acknowledgement()
    {
        var job = NewJob();
        job.BeginEscalating(2, DateTimeOffset.UnixEpoch.AddMinutes(5));

        job.SettleEscalation();

        job.Status.Should().Be(NotificationJobStatus.Expanded);
        job.EscalationNextDueAtUtc.Should().BeNull();
        job.AcknowledgedAtUtc.Should().BeNull();
    }

    [Fact]
    public void A_plain_expanded_job_is_not_escalating()
    {
        var job = NewJob();
        job.MarkExpanded();

        job.IsEscalating.Should().BeFalse();
        job.EscalationStepCount.Should().Be(0);
    }
}
