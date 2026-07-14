using System.Diagnostics.Metrics;
using FluentAssertions;
using WireHQ.Modules.Orchestration.Domain;
using WireHQ.Modules.Orchestration.Observability;
using Xunit;

namespace WireHQ.Orchestration.UnitTests;

/// <summary>
/// The deployment-engine metrics (docs/15 §7): a job that reaches a terminal state records a
/// <c>wirehq.jobs.processed</c> count + a <c>wirehq.jobs.duration</c> sample, tagged by type and outcome;
/// a non-terminal job records nothing (so callers can fire it unconditionally after a save). Host-free so the
/// process-global meter has no background emitters — verified by listening to the real meter.
/// </summary>
public sealed class OrchestrationMetricsTests
{
    [Fact]
    public void Records_processed_and_duration_for_a_succeeded_job()
    {
        using var probe = new MeterProbe();

        var job = NewJob();
        job.MarkDispatched(DateTimeOffset.UtcNow);
        job.Succeed(DateTimeOffset.UtcNow);

        OrchestrationMetrics.RecordCompleted(job);

        probe.Count("wirehq.jobs.processed").Should().Be(1);
        probe.Outcomes("wirehq.jobs.processed").Should().ContainSingle().Which.Should().Be("succeeded");
        probe.Types("wirehq.jobs.processed").Should().ContainSingle().Which.Should().Be(nameof(DeploymentJobType.DeployConfig));
        probe.Count("wirehq.jobs.duration").Should().Be(1);
    }

    [Fact]
    public void Records_the_failed_outcome_for_a_failed_job()
    {
        using var probe = new MeterProbe();

        var job = NewJob();
        job.MarkDispatched(DateTimeOffset.UtcNow);
        job.Fail(DateTimeOffset.UtcNow, "boom");

        OrchestrationMetrics.RecordCompleted(job);

        probe.Outcomes("wirehq.jobs.processed").Should().ContainSingle().Which.Should().Be("failed");
    }

    [Fact]
    public void Is_a_noop_for_a_non_terminal_job()
    {
        using var probe = new MeterProbe();

        var job = NewJob();
        job.MarkDispatched(DateTimeOffset.UtcNow); // Dispatched — not terminal

        OrchestrationMetrics.RecordCompleted(job);

        probe.Count("wirehq.jobs.processed").Should().Be(0);
        probe.Count("wirehq.jobs.duration").Should().Be(0);
    }

    private static DeploymentJob NewJob() => DeploymentJob.Queue(
        Guid.NewGuid(), Guid.NewGuid(), DeploymentJobType.DeployConfig, 1, $"idem-{Guid.NewGuid():N}", "corr", DateTimeOffset.UtcNow);

    /// <summary>Captures measurements from the WireHQ.Orchestration meter for the lifetime of a test.</summary>
    private sealed class MeterProbe : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly List<(string Instrument, string Type, string Outcome)> _measurements = [];
        private readonly Lock _gate = new();

        public MeterProbe()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == OrchestrationMetrics.MeterName)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((inst, _, tags, _) => Capture(inst.Name, tags));
            _listener.SetMeasurementEventCallback<double>((inst, _, tags, _) => Capture(inst.Name, tags));
            _listener.Start();
        }

        private void Capture(string instrument, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            string type = "", outcome = "";
            foreach (var tag in tags)
            {
                if (tag.Key == "type") type = tag.Value?.ToString() ?? "";
                else if (tag.Key == "outcome") outcome = tag.Value?.ToString() ?? "";
            }

            lock (_gate)
            {
                _measurements.Add((instrument, type, outcome));
            }
        }

        public int Count(string instrument)
        {
            lock (_gate)
            {
                return _measurements.Count(m => m.Instrument == instrument);
            }
        }

        public IReadOnlyList<string> Outcomes(string instrument)
        {
            lock (_gate)
            {
                return _measurements.Where(m => m.Instrument == instrument).Select(m => m.Outcome).ToList();
            }
        }

        public IReadOnlyList<string> Types(string instrument)
        {
            lock (_gate)
            {
                return _measurements.Where(m => m.Instrument == instrument).Select(m => m.Type).ToList();
            }
        }

        public void Dispose() => _listener.Dispose();
    }
}
