using System.Diagnostics;

namespace WireHQ.Modules.Orchestration.Observability;

/// <summary>One structured edge event: a named step with an outcome, timing, and optional attributes (docs/15 §9).</summary>
public sealed record AgentDiagnosticEvent(
    string Name,
    DateTimeOffset AtUtc,
    double? DurationMs,
    string? Level,
    string? Outcome,
    string? Message,
    IReadOnlyDictionary<string, string>? Attributes);

/// <summary>
/// Re-emits an agent's structured step events as an OpenTelemetry span on the <see cref="AgentTelemetry"/> source
/// (docs/15 §9, Phase 5). One span per batch, with each step as a span event; **parented to the originating
/// deploy's trace id** (so the edge steps nest under the browser → API → job trace, ADR-030) and **tagged with
/// the tenant/agent identity supplied by the gateway from the client certificate** (never from the agent body).
/// Pure + host-free so the parenting/tagging is directly testable; returns <c>null</c> when the source is not
/// sampled (no Collector configured).
/// </summary>
public static class AgentTelemetryEmitter
{
    public static Activity? EmitSpan(
        Guid agentId,
        Guid organizationId,
        Guid? instanceId,
        Guid? jobId,
        string? traceId,
        IReadOnlyList<AgentDiagnosticEvent> events)
    {
        if (events.Count == 0)
        {
            return null;
        }

        var parent = TryParseTraceParent(traceId);
        var start = events[0].AtUtc;
        var activity = AgentTelemetry.Source.StartActivity(
            jobId is null ? "agent.diagnostics" : "agent.deploy", ActivityKind.Consumer, parent, startTime: start);
        if (activity is null)
        {
            return null; // not sampled (no Collector configured)
        }

        activity.SetTag("agent.id", agentId.ToString());
        activity.SetTag("org.id", organizationId.ToString());
        if (instanceId is { } iid)
        {
            activity.SetTag("instance.id", iid.ToString());
        }

        if (jobId is { } jid)
        {
            activity.SetTag("job.id", jid.ToString());
        }

        var anyFailure = false;
        var lastAt = start;
        foreach (var e in events)
        {
            var tags = new ActivityTagsCollection { ["outcome"] = e.Outcome, ["level"] = e.Level };
            if (e.DurationMs is { } d)
            {
                tags["duration_ms"] = d;
            }

            if (!string.IsNullOrWhiteSpace(e.Message))
            {
                tags["message"] = e.Message;
            }

            foreach (var (k, v) in e.Attributes ?? new Dictionary<string, string>())
            {
                tags[$"attr.{k}"] = v;
            }

            activity.AddEvent(new ActivityEvent(e.Name, e.AtUtc, tags));
            anyFailure |= IsFailure(e.Outcome);
            if (e.AtUtc > lastAt)
            {
                lastAt = e.AtUtc;
            }
        }

        activity.SetStatus(anyFailure ? ActivityStatusCode.Error : ActivityStatusCode.Ok);
        activity.SetEndTime(lastAt.UtcDateTime);
        activity.Dispose(); // stop the span (end time already set) so it exports
        return activity;
    }

    public static bool IsFailure(string? outcome) =>
        outcome is not null && (outcome.Equals("failed", StringComparison.OrdinalIgnoreCase)
            || outcome.Equals("error", StringComparison.OrdinalIgnoreCase));

    // Build a remote parent context from the deploy's W3C trace id (32 hex). Invalid/absent ⇒ a root span.
    private static ActivityContext TryParseTraceParent(string? traceId)
    {
        if (string.IsNullOrWhiteSpace(traceId))
        {
            return default;
        }

        try
        {
            return new ActivityContext(
                ActivityTraceId.CreateFromString(traceId.AsSpan()),
                ActivitySpanId.CreateRandom(),
                ActivityTraceFlags.Recorded,
                isRemote: true);
        }
        catch (ArgumentOutOfRangeException)
        {
            return default;
        }
        catch (FormatException)
        {
            return default;
        }
    }
}
