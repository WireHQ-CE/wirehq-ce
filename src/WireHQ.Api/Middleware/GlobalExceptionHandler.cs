using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using WireHQ.Shared.Observability;

namespace WireHQ.Api.Middleware;

/// <summary>
/// Last-resort handler for UNEXPECTED exceptions (bugs/outages). Expected failures never reach
/// here — they flow through Result → ProblemDetails. Returns a generic 500 with a traceId only;
/// no stack traces or internal details leak to clients. (docs/06-api-design.md)
///
/// Two otherwise-unhandled exceptions are mapped to a meaningful status: a
/// <see cref="DbUpdateConcurrencyException"/> (an optimistic-concurrency token mismatch — two
/// requests raced to update the same row; the xmin row-version guard fired), and a Postgres
/// unique-constraint violation (23505 — two requests raced to claim the same uniquely-indexed slot;
/// e.g. the licensing partial unique index enforcing one live activation per licence). Both become a
/// 409 Conflict so the client can reload and retry, instead of a misleading 500.
/// </summary>
public sealed class GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var traceId = CorrelationId.Current() ?? httpContext.TraceIdentifier;

        if (exception is DbUpdateConcurrencyException ||
            exception is DbUpdateException { InnerException: Npgsql.PostgresException { SqlState: "23505" } })
        {
            logger.LogWarning(exception, "Concurrency conflict. TraceId={TraceId}", traceId);

            var conflict = new ProblemDetails
            {
                Status = StatusCodes.Status409Conflict,
                Title = "The resource was modified by another request.",
                Type = "https://docs.wirehq.io/errors/conflict",
                Detail = "Your copy is out of date. Reload the latest version and try again.",
            };
            conflict.Extensions["code"] = "concurrency_conflict";
            conflict.Extensions["traceId"] = traceId;

            httpContext.Response.StatusCode = StatusCodes.Status409Conflict;
            await httpContext.Response.WriteAsJsonAsync(conflict, cancellationToken);
            return true;
        }

        logger.LogError(exception, "Unhandled exception. TraceId={TraceId}", traceId);

        var problem = new ProblemDetails
        {
            Status = StatusCodes.Status500InternalServerError,
            Title = "An unexpected error occurred.",
            Type = "https://docs.wirehq.io/errors/internal",
            Detail = "The request could not be completed. Reference the traceId when contacting support.",
        };
        problem.Extensions["code"] = "internal_error";
        problem.Extensions["traceId"] = traceId;

        httpContext.Response.StatusCode = StatusCodes.Status500InternalServerError;
        await httpContext.Response.WriteAsJsonAsync(problem, cancellationToken);
        return true;
    }
}
