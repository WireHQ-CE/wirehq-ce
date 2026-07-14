using System.Diagnostics;

namespace WireHQ.Modules.Orchestration.Observability;

/// <summary>
/// The edge (agent) OpenTelemetry <see cref="ActivitySource"/> (docs/15 §9, Phase 5). The mTLS agent is
/// outbound-only and never reaches the Collector directly (ADR-028), so it posts its structured step events to
/// the gateway (<c>/agent/v1/diagnostics</c>), which re-emits them as spans on this source — **parented to the
/// originating deploy's trace id** so the agent's apply/verify/wg steps nest under the same browser → API → job
/// trace (ADR-030), and **tagged with the tenant/agent identity from the client certificate** (never trusted
/// from the agent body). Registered on the tracer in the host (<c>AddObservability</c> calls <c>AddSource</c>
/// with <see cref="ActivitySourceName"/>), so it exports over the existing OTLP pipeline; when no Collector is
/// configured it is simply not sampled.
/// </summary>
public static class AgentTelemetry
{
    public const string ActivitySourceName = "WireHQ.Agent";

    public static readonly ActivitySource Source = new(ActivitySourceName);
}
