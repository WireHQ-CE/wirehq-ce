using System.Diagnostics.Metrics;

namespace WireHQ.Application.Common.Observability;

/// <summary>
/// Counters for the status self-probe (docs/20-status-page.md §4/§10, docs/15 §19) — the sampler's liveness
/// and what it observed. Registered on the metrics SDK in the host (<c>AddObservability</c> → <c>AddMeter</c>),
/// so these export over the same OTLP pipeline; a component flapping is already SLO-alert-worthy.
/// Cardinality-safe: the only dimensions are <c>component</c> (a bounded registry) and <c>state</c> (three
/// values) — never an id.
///
/// Defined in the core layer (not the SaaS-only <c>Api/Status</c> tree) so the host's <c>AddMeter</c> line
/// compiles in the Community Edition, where the meter simply stays idle — the self-probe that increments it is
/// stripped there (the CE keeps its inline status endpoint).
/// </summary>
public static class StatusMetrics
{
    public const string MeterName = "WireHQ.Status";

    public static readonly Meter Meter = new(MeterName);

    /// <summary>A self-probe sample cycle completed — the sampler liveness signal.</summary>
    public static readonly Counter<long> ProbeRuns = Meter.CreateCounter<long>(
        "wirehq.status.probe_runs",
        unit: "{run}",
        description: "Status self-probe sample cycles completed.");

    /// <summary>A component observation written, tagged by <c>component</c> and <c>state</c> (operational/degraded/down).</summary>
    public static readonly Counter<long> ComponentSamples = Meter.CreateCounter<long>(
        "wirehq.status.component_samples",
        unit: "{sample}",
        description: "Status component observations written, by component and state.");

    /// <summary>A daily-uptime rollup was computed (idempotent upsert) — the compaction liveness signal.</summary>
    public static readonly Counter<long> Rollups = Meter.CreateCounter<long>(
        "wirehq.status.rollups",
        unit: "{rollup}",
        description: "Status daily-uptime rollups computed.");
}
