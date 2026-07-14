using System.Diagnostics;
using FluentAssertions;
using WireHQ.Modules.Orchestration.Observability;
using Xunit;

namespace WireHQ.Api.IntegrationTests;

/// <summary>
/// Host-free unit tests for the Phase-5 edge span emission (docs/15 §9): the agent's step events become one
/// OTel span on the <c>WireHQ.Agent</c> source, tagged with the cert identity and parented to the deploy trace
/// so the edge steps join the browser → API → job trace.
/// </summary>
public sealed class AgentTelemetryEmitterTests : IDisposable
{
    private readonly List<Activity> _captured = [];
    private readonly ActivityListener _listener;

    public AgentTelemetryEmitterTests()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == AgentTelemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => _captured.Add(activity),
        };
        ActivitySource.AddActivityListener(_listener);
    }

    private static AgentDiagnosticEvent Event(string name, string outcome = "ok", double? ms = 5, string? level = "info") =>
        new(name, DateTimeOffset.Parse("2026-06-30T10:00:00Z"), ms, level, outcome, null, null);

    [Fact]
    public void Emits_one_span_parented_to_the_deploy_trace_with_steps_as_span_events()
    {
        var agentId = Guid.NewGuid();
        var orgId = Guid.NewGuid();
        var instanceId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        const string traceId = "0af7651916cd43dd8448eb211c80319c";

        AgentTelemetryEmitter.EmitSpan(
            agentId, orgId, instanceId, jobId, traceId,
            [Event("verify"), Event("apply"), Event("telemetry")]);

        var span = _captured.Should().ContainSingle().Subject;
        span.OperationName.Should().Be("agent.deploy");
        span.TraceId.ToString().Should().Be(traceId, "the edge span joins the deploy trace");
        span.GetTagItem("agent.id").Should().Be(agentId.ToString());
        span.GetTagItem("org.id").Should().Be(orgId.ToString());
        span.GetTagItem("instance.id").Should().Be(instanceId.ToString());
        span.GetTagItem("job.id").Should().Be(jobId.ToString());
        span.Events.Select(e => e.Name).Should().Equal("verify", "apply", "telemetry");
        span.Status.Should().Be(ActivityStatusCode.Ok);
    }

    [Fact]
    public void Marks_the_span_failed_when_a_step_failed()
    {
        AgentTelemetryEmitter.EmitSpan(
            Guid.NewGuid(), Guid.NewGuid(), null, Guid.NewGuid(), "0af7651916cd43dd8448eb211c80319c",
            [Event("verify"), Event("apply", outcome: "failed", level: "error")]);

        _captured.Should().ContainSingle().Which.Status.Should().Be(ActivityStatusCode.Error);
    }

    [Fact]
    public void A_batch_with_no_job_and_no_trace_is_a_root_span()
    {
        AgentTelemetryEmitter.EmitSpan(Guid.NewGuid(), Guid.NewGuid(), null, null, null, [Event("heartbeat")]);

        var span = _captured.Should().ContainSingle().Subject;
        span.OperationName.Should().Be("agent.diagnostics");
        span.Parent.Should().BeNull("no deploy trace id ⇒ a root span");
    }

    [Fact]
    public void An_invalid_trace_id_degrades_to_a_root_span_rather_than_throwing()
    {
        var act = () => AgentTelemetryEmitter.EmitSpan(
            Guid.NewGuid(), Guid.NewGuid(), null, Guid.NewGuid(), "not-a-valid-trace-id", [Event("apply")]);

        act.Should().NotThrow();
        _captured.Should().ContainSingle().Which.ParentSpanId.ToString().Should().Be("0000000000000000");
    }

    public void Dispose() => _listener.Dispose();
}
