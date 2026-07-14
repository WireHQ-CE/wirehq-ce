using System.Diagnostics;
using MediatR;
using WireHQ.Application.Common.Observability;
using WireHQ.Shared.Results;

namespace WireHQ.Application.Common.Behaviors;

/// <summary>
/// Opens a tracing span per use case (docs/15 §6). The span nests under the ASP.NET request span (or a
/// background job's activity), so it inherits the trace id that is the correlation reference (ADR-030) —
/// one trace ties the HTTP request, the use case, and (with EF/Npgsql instrumentation) its DB work
/// together. Outermost behavior, so the span is the parent of all the others' logs and the handler's work.
/// The span records the outcome: <c>ok</c>, or the failure's stable <c>code</c> with an Error status — so a
/// trace backend can find failed use cases without log scraping. Cardinality-safe: the span name is the
/// request type (a bounded set), never an id (docs/15 §6 attribute policy).
/// </summary>
public sealed class TracingBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
    where TResponse : Result
{
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        using var activity = ApplicationTelemetry.Source.StartActivity(typeof(TRequest).Name, ActivityKind.Internal);

        var response = await next();

        if (activity is not null)
        {
            if (response.IsSuccess)
            {
                activity.SetTag("wirehq.outcome", "ok");
                activity.SetStatus(ActivityStatusCode.Ok);
            }
            else
            {
                activity.SetTag("wirehq.outcome", response.Error.Code);
                activity.SetStatus(ActivityStatusCode.Error, response.Error.Code);
            }
        }

        return response;
    }
}
