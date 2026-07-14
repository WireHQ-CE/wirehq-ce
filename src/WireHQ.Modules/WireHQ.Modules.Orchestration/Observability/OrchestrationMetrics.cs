using System.Diagnostics;
using System.Diagnostics.Metrics;
using WireHQ.Modules.Orchestration.Domain;

namespace WireHQ.Modules.Orchestration.Observability;

/// <summary>
/// Deployment-engine metrics (docs/15 §7): job throughput + latency by type and outcome, dispatcher liveness,
/// and the pending-queue depth. Registered on the metrics SDK in the host (<c>AddObservability</c> calls
/// <c>AddMeter</c> with <see cref="MeterName"/>), so it exports over the same OTLP pipeline as traces/logs.
/// Cardinality-safe: the only tags are the job type + outcome (both bounded), never an id.
/// </summary>
public static class OrchestrationMetrics
{
    public const string MeterName = "WireHQ.Orchestration";

    public static readonly Meter Meter = new(MeterName);

    private static readonly Counter<long> JobsProcessed = Meter.CreateCounter<long>(
        "wirehq.jobs.processed",
        unit: "{job}",
        description: "Deployment jobs driven to a terminal state, by type and outcome.");

    private static readonly Histogram<double> JobDuration = Meter.CreateHistogram<double>(
        "wirehq.jobs.duration",
        unit: "s",
        description: "Deployment job wall-clock duration (queued → terminal) in seconds, by type and outcome.");

    /// <summary>Liveness: a non-zero rate means the dispatcher loop is turning.</summary>
    public static readonly Counter<long> DispatcherRuns = Meter.CreateCounter<long>(
        "wirehq.jobs.dispatcher.runs",
        unit: "{pass}",
        description: "Deployment-dispatcher passes — a non-zero rate means the job engine is alive.");

    private static int _queueGaugeRegistered;

    /// <summary>
    /// Record a job that has just reached a terminal state (Succeeded/Failed/RolledBack); a no-op for any
    /// non-terminal status, so callers can fire it unconditionally after a save without checking the state.
    /// </summary>
    public static void RecordCompleted(DeploymentJob job)
    {
        var outcome = job.Status switch
        {
            DeploymentJobStatus.Succeeded => "succeeded",
            DeploymentJobStatus.Failed => "failed",
            DeploymentJobStatus.RolledBack => "rolled_back",
            _ => null,
        };
        if (outcome is null)
        {
            return;
        }

        var tags = new TagList { { "type", job.Type.ToString() }, { "outcome", outcome } };
        JobsProcessed.Add(1, tags);

        if (job.CompletedAtUtc is { } completed)
        {
            JobDuration.Record(Math.Max(0d, (completed - job.CreatedAtUtc).TotalSeconds), tags);
        }
    }

    /// <summary>
    /// Register the pending-queue-depth observable gauge exactly once (idempotent across hosts/tests). The
    /// <paramref name="observe"/> callback is invoked by the metrics SDK on each collection — keep it cheap.
    /// </summary>
    public static void RegisterQueueDepthGauge(Func<long> observe)
    {
        if (Interlocked.Exchange(ref _queueGaugeRegistered, 1) == 1)
        {
            return;
        }

        Meter.CreateObservableGauge(
            "wirehq.jobs.queue_depth",
            observe,
            unit: "{job}",
            description: "Deployment jobs currently pending (queued, not yet claimed).");
    }
}
