using System.Diagnostics;
using MediatR;
using Microsoft.Extensions.Logging;
using WireHQ.Shared.Results;

namespace WireHQ.Application.Common.Behaviors;

/// <summary>
/// Structured, uniform logging + timing around every use case. Logs the outcome (success or the
/// failure's stable <c>code</c>) and elapsed time. Correlation/trace ids are attached by Serilog
/// enrichers at the host. (docs/10-deployment.md)
/// </summary>
public sealed class RequestLoggingBehavior<TRequest, TResponse>(ILogger<RequestLoggingBehavior<TRequest, TResponse>> logger)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
    where TResponse : Result
{
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        var requestName = typeof(TRequest).Name;
        var stopwatch = Stopwatch.GetTimestamp();

        logger.LogInformation("Handling {RequestName}", requestName);

        var response = await next();

        var elapsedMs = Stopwatch.GetElapsedTime(stopwatch).TotalMilliseconds;

        if (response.IsSuccess)
        {
            logger.LogInformation("Handled {RequestName} in {ElapsedMs:0.0}ms", requestName, elapsedMs);
        }
        else
        {
            logger.LogWarning(
                "Handled {RequestName} in {ElapsedMs:0.0}ms with failure {ErrorCode}",
                requestName, elapsedMs, response.Error.Code);
        }

        return response;
    }
}
