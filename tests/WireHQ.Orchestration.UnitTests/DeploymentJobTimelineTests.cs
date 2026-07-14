using FluentAssertions;
using WireHQ.Modules.Orchestration.Domain;
using Xunit;

namespace WireHQ.Orchestration.UnitTests;

/// <summary>
/// The deployment lifecycle timeline (docs/15 §8, Phase 5): a normal deploy leads with <c>queued</c>; a
/// drift-triggered auto-re-converge job leads with <c>drift_detected → reconverge_requested → queued</c> so the
/// customer timeline shows WHY the redeploy happened before the usual dispatched → applying → succeeded flow.
/// </summary>
public sealed class DeploymentJobTimelineTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-07-02T10:00:00Z");

    [Fact]
    public void A_normal_deploy_starts_with_queued_only()
    {
        var job = DeploymentJob.Queue(
            Guid.NewGuid(), Guid.NewGuid(), DeploymentJobType.DeployConfig, 1, "idem", "corr", Now);

        job.Events.Select(e => e.Phase).Should().Equal("queued");
    }

    [Fact]
    public void An_auto_reconverge_job_leads_with_drift_detected_then_reconverge_requested_then_queued()
    {
        var job = DeploymentJob.Queue(
            Guid.NewGuid(), Guid.NewGuid(), DeploymentJobType.DeployConfig, 1, "idem", "corr", Now,
            reconvergeReason: "Config drift detected on the instance.");

        job.Events.Select(e => e.Phase).Should().Equal("drift_detected", "reconverge_requested", "queued");
        job.Events.First(e => e.Phase == "drift_detected").Detail.Should().Be("Config drift detected on the instance.");
    }

    [Fact]
    public void The_full_reconverge_timeline_reads_end_to_end()
    {
        var job = DeploymentJob.Queue(
            Guid.NewGuid(), Guid.NewGuid(), DeploymentJobType.DeployConfig, 1, "idem", "corr", Now,
            reconvergeReason: "Config drift detected on the instance.");
        job.MarkDispatched(Now.AddSeconds(1));
        job.MarkApplying(Now.AddSeconds(2));
        job.Succeed(Now.AddSeconds(3), "Applied by agent.");

        job.Events.Select(e => e.Phase).Should()
            .Equal("drift_detected", "reconverge_requested", "queued", "dispatched", "applying", "succeeded");
    }
}
