using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using WireHQ.Shared.Results;

namespace WireHQ.Modules.WireGuard.Endpoints;

/// <summary>
/// Maps a use-case <see cref="Result"/> to a minimal-API <see cref="IResult"/> with RFC 9457
/// ProblemDetails — the minimal-API analogue of the core <c>ApiControllerBase</c>, so module
/// endpoints share the platform's error contract.
/// </summary>
public static class ResultEndpointExtensions
{
    public static IResult ToHttpResult(this Result result, int successStatusCode = StatusCodes.Status204NoContent) =>
        result.IsSuccess ? Results.StatusCode(successStatusCode) : result.Error.ToProblem();

    public static IResult ToHttpResult<T>(this Result<T> result, int successStatusCode = StatusCodes.Status200OK) =>
        result.IsSuccess ? Results.Json(result.Value, statusCode: successStatusCode) : result.Error.ToProblem();

    /// <summary>Map a successful result via a custom producer (e.g. a file/QR download); failures still go to ProblemDetails.</summary>
    public static IResult ToHttpResult<T>(this Result<T> result, Func<T, IResult> onSuccess) =>
        result.IsSuccess ? onSuccess(result.Value) : result.Error.ToProblem();

    private static IResult ToProblem(this Error error)
    {
        var status = error.Type switch
        {
            ErrorType.Validation => StatusCodes.Status400BadRequest,
            ErrorType.Unauthorized => StatusCodes.Status401Unauthorized,
            ErrorType.Forbidden => StatusCodes.Status403Forbidden,
            ErrorType.NotFound => StatusCodes.Status404NotFound,
            ErrorType.Conflict => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status500InternalServerError,
        };

        var problem = new ProblemDetails
        {
            Status = status,
            Title = error.Description,
            Type = $"https://docs.wirehq.io/errors/{error.Code}",
        };
        problem.Extensions["code"] = error.Code;
        // `traceId` (the correlation reference) is set centrally by the ProblemDetails customizer
        // (ApiServiceCollectionExtensions, ADR-030) — which runs for this minimal-API problem too.

        if (error is ValidationError validation)
        {
            problem.Title = "One or more validation errors occurred.";
            problem.Extensions["errors"] = validation.Errors;
        }

        return Results.Problem(problem);
    }
}
