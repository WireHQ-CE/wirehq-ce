using System.Diagnostics.Metrics;

namespace WireHQ.Application.Common.Observability;

/// <summary>
/// The Application layer's OpenTelemetry <see cref="Meter"/> — RED metrics per use case (docs/15 §7).
/// Registered on the metrics SDK in the host (<c>AddObservability</c> calls <c>AddMeter</c> with
/// <see cref="MeterName"/>), so the measurements export over the same OTLP pipeline as traces and logs.
/// Cardinality-safe by construction: the only dimensions are the use-case name (the request type — a bounded
/// set) and the outcome, never an id (docs/15 §6/§7 attribute policy).
/// </summary>
public static class ApplicationMetrics
{
    public const string MeterName = "WireHQ.Application";

    public static readonly Meter Meter = new(MeterName);

    /// <summary>Count of use-case executions, tagged by <c>usecase</c> and <c>outcome</c> (ok / the failure's code).</summary>
    public static readonly Counter<long> UseCaseExecutions = Meter.CreateCounter<long>(
        "wirehq.usecase.executions",
        unit: "{execution}",
        description: "Number of use cases executed, by use case and outcome.");

    /// <summary>Use-case wall-clock duration in seconds, tagged by <c>usecase</c> and <c>outcome</c> — the RED latency signal.</summary>
    public static readonly Histogram<double> UseCaseDuration = Meter.CreateHistogram<double>(
        "wirehq.usecase.duration",
        unit: "s",
        description: "Use-case handler duration in seconds, by use case and outcome.");
}
