using Serilog.Context;
using WireHQ.Application.Abstractions;
using WireHQ.Shared.Observability;

namespace WireHQ.Api.Middleware;

/// <summary>
/// Stamps the request's correlation identity onto observability: echoes the W3C trace id as the
/// <c>X-Correlation-Id</c> response header (a quotable support reference) and pushes correlation +
/// tenant + actor into the Serilog <c>LogContext</c>, so every log line emitted while handling the
/// request is attributable to an org and user. Runs after authentication + tenant resolution, so the
/// org and user are known. (docs/15-observability.md, ADR-030)
/// </summary>
public sealed class ObservabilityContextMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, ITenantContext tenant, ICurrentUser user)
    {
        var correlationId = CorrelationId.Current() ?? context.TraceIdentifier;

        context.Response.OnStarting(() =>
        {
            context.Response.Headers["X-Correlation-Id"] = correlationId;
            return Task.CompletedTask;
        });

        var source = !user.IsAuthenticated
            ? "anonymous"
            : user.ImpersonatorUserId is null ? "user" : "impersonation";

        using (LogContext.PushProperty("CorrelationId", correlationId))
        using (LogContext.PushProperty("OrgId", tenant.OrganizationId))
        using (LogContext.PushProperty("UserId", user.UserId))
        using (LogContext.PushProperty("Source", source))
        {
            await next(context);
        }
    }
}
