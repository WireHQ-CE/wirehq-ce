using Serilog.Core;
using Serilog.Events;
using WireHQ.Application.Abstractions;

namespace WireHQ.Api.Observability;

/// <summary>
/// Per-tenant diagnostic verbosity (docs/15 §4). The logger captures WireHQ logs down to Debug; this filter
/// keeps Information+ for everyone but lets Debug/Verbose through ONLY for tenants currently in diagnostic mode
/// — so support can raise one tenant's verbosity, time-boxed, without a redeploy. Reads the <c>OrgId</c> the
/// <c>ObservabilityContextMiddleware</c> puts on the log scope.
/// </summary>
public sealed class DiagnosticLogFilter(IDiagnosticModeStore store) : ILogEventFilter
{
    public bool IsEnabled(LogEvent logEvent)
    {
        if (logEvent.Level >= LogEventLevel.Information)
        {
            return true;
        }

        // Below the normal level: only emit for a tenant whose diagnostic window is open.
        return logEvent.Properties.TryGetValue("OrgId", out var value)
            && value is ScalarValue { Value: { } raw }
            && Guid.TryParse(raw.ToString(), out var organizationId)
            && store.IsEnabled(organizationId);
    }
}
