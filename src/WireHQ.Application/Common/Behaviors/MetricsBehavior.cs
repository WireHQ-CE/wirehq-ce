using System.Diagnostics;
using MediatR;
using WireHQ.Application.Common.Observability;
using WireHQ.Shared.Results;

namespace WireHQ.Application.Common.Behaviors;

/// <summary>
/// Records RED metrics per use case (docs/15 §7): an executions counter + a duration histogram on the
/// <see cref="ApplicationMetrics"/> meter, tagged by the use case (the request type name — a bounded set) and
/// the outcome (<c>ok</c>, or the failure's stable <c>code</c>). Sits just inside <see cref="TracingBehavior{TRequest,TResponse}"/>
/// so it measures the same end-to-end scope — covering the whole pipeline (validation, auth, handler), and
/// background-dispatched use cases too, not just HTTP routes. Cardinality-safe: tag values are bounded, never
/// an id (docs/15 §6/§7 attribute policy).
/// </summary>
public sealed class MetricsBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
    where TResponse : Result
{
    // Resolved once per closed generic — the request type name is a stable, bounded label.
    private static readonly string UseCase = typeof(TRequest).Name;

    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        var startedAt = Stopwatch.GetTimestamp();

        var response = await next();

        var elapsed = Stopwatch.GetElapsedTime(startedAt);
        var outcome = response.IsSuccess ? "ok" : response.Error.Code;
        var tags = new TagList { { "usecase", UseCase }, { "outcome", outcome } };

        ApplicationMetrics.UseCaseExecutions.Add(1, tags);
        ApplicationMetrics.UseCaseDuration.Record(elapsed.TotalSeconds, tags);

        return response;
    }
}
