using MediatR;
using Microsoft.AspNetCore.Mvc;
using WireHQ.Shared.Observability;
using WireHQ.Shared.Results;

namespace WireHQ.Api.Controllers;

/// <summary>
/// Base controller that turns a use-case <see cref="Result"/> into an HTTP response, mapping a
/// domain <see cref="Error"/> to the right status + RFC 9457 ProblemDetails — in ONE place, so
/// every endpoint behaves identically. Controllers stay thin: dispatch a command/query, return
/// the result. (docs/06-api-design.md)
/// </summary>
[ApiController]
public abstract class ApiControllerBase : ControllerBase
{
    private ISender? _sender;

    protected ISender Sender => _sender ??= HttpContext.RequestServices.GetRequiredService<ISender>();

    protected IActionResult Ok<T>(Result<T> result) =>
        result.IsSuccess ? base.Ok(result.Value) : Problem(result.Error);

    protected IActionResult NoContent(Result result) =>
        result.IsSuccess ? base.NoContent() : Problem(result.Error);

    protected IActionResult Created<T>(Result<T> result) =>
        result.IsSuccess
            ? StatusCode(StatusCodes.Status201Created, result.Value)
            : Problem(result.Error);

    protected IActionResult Problem(Error error)
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
        problem.Extensions["traceId"] = CorrelationId.Current() ?? HttpContext.TraceIdentifier;

        if (error is ValidationError validation)
        {
            problem.Title = "One or more validation errors occurred.";
            problem.Extensions["errors"] = validation.Errors;
        }

        return StatusCode(status, problem);
    }
}
