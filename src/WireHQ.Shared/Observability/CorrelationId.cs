using System.Diagnostics;

namespace WireHQ.Shared.Observability;

/// <summary>
/// The single correlation identity for a request or background operation: the active W3C trace id
/// (<see cref="Activity"/>). It is surfaced consistently in logs, in ProblemDetails (<c>traceId</c>),
/// in the <c>X-Correlation-Id</c> response header, and as the audit <c>RequestId</c> — so one
/// customer-quotable reference ties the browser, API, database, background jobs, and agent together.
/// (docs/15-observability.md, ADR-030)
/// </summary>
public static class CorrelationId
{
    /// <summary>
    /// The active W3C trace id as a 32-char hex string, or <c>null</c> when no <see cref="Activity"/>
    /// is recording (callers fall back to a context-specific id, e.g. <c>HttpContext.TraceIdentifier</c>).
    /// </summary>
    public static string? Current()
    {
        var activity = Activity.Current;
        return activity is not null && activity.TraceId != default
            ? activity.TraceId.ToString()
            : null;
    }
}
