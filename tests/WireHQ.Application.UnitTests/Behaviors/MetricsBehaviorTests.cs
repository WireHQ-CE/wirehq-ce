using System.Diagnostics.Metrics;
using FluentAssertions;
using MediatR;
using WireHQ.Application.Common.Behaviors;
using WireHQ.Application.Common.Messaging;
using WireHQ.Application.Common.Observability;
using WireHQ.Shared.Results;
using Xunit;

namespace WireHQ.Application.UnitTests.Behaviors;

/// <summary>
/// The <see cref="MetricsBehavior{TRequest,TResponse}"/> (docs/15 §7): every use case records an executions
/// counter + a duration histogram on the WireHQ.Application meter, tagged by the use case and the outcome
/// (<c>ok</c> on success, the failure's stable error code otherwise). Verified by listening to the real meter.
/// </summary>
public sealed class MetricsBehaviorTests
{
    [Fact]
    public async Task Records_an_execution_and_a_duration_tagged_ok_on_success()
    {
        using var probe = new MeterProbe();

        var behavior = new MetricsBehavior<ProbeCommand, Result>();
        await behavior.Handle(new ProbeCommand(), Next(Result.Success()), CancellationToken.None);

        probe.Counter("wirehq.usecase.executions", nameof(ProbeCommand)).Should().Be(1);
        probe.Outcomes("wirehq.usecase.executions", nameof(ProbeCommand)).Should().ContainSingle().Which.Should().Be("ok");
        probe.HistogramCount("wirehq.usecase.duration", nameof(ProbeCommand)).Should().Be(1);
    }

    [Fact]
    public async Task Records_the_failure_code_as_the_outcome_on_failure()
    {
        using var probe = new MeterProbe();

        var behavior = new MetricsBehavior<ProbeCommand, Result>();
        await behavior.Handle(new ProbeCommand(), Next(Result.Failure(Error.Validation("probe.invalid", "nope"))), CancellationToken.None);

        probe.Outcomes("wirehq.usecase.executions", nameof(ProbeCommand)).Should().ContainSingle().Which.Should().Be("probe.invalid");
    }

    private static RequestHandlerDelegate<Result> Next(Result result) => _ => Task.FromResult(result);

    private sealed record ProbeCommand : IBaseCommand;

    /// <summary>Captures measurements from the WireHQ.Application meter for the lifetime of a test.</summary>
    private sealed class MeterProbe : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly List<(string Instrument, double Value, string UseCase, string Outcome)> _measurements = [];
        private readonly Lock _gate = new();

        public MeterProbe()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == ApplicationMetrics.MeterName)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((inst, value, tags, _) => Capture(inst.Name, value, tags));
            _listener.SetMeasurementEventCallback<double>((inst, value, tags, _) => Capture(inst.Name, value, tags));
            _listener.Start();
        }

        private void Capture(string instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            string useCase = "", outcome = "";
            foreach (var tag in tags)
            {
                if (tag.Key == "usecase") useCase = tag.Value?.ToString() ?? "";
                else if (tag.Key == "outcome") outcome = tag.Value?.ToString() ?? "";
            }

            lock (_gate)
            {
                _measurements.Add((instrument, value, useCase, outcome));
            }
        }

        public long Counter(string instrument, string useCase)
        {
            lock (_gate)
            {
                return (long)_measurements.Where(m => m.Instrument == instrument && m.UseCase == useCase).Sum(m => m.Value);
            }
        }

        public int HistogramCount(string instrument, string useCase)
        {
            lock (_gate)
            {
                return _measurements.Count(m => m.Instrument == instrument && m.UseCase == useCase);
            }
        }

        public IReadOnlyList<string> Outcomes(string instrument, string useCase)
        {
            lock (_gate)
            {
                return _measurements.Where(m => m.Instrument == instrument && m.UseCase == useCase).Select(m => m.Outcome).ToList();
            }
        }

        public void Dispose() => _listener.Dispose();
    }
}
