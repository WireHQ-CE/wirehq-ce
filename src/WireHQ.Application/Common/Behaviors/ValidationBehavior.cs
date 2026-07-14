using FluentValidation;
using MediatR;
using WireHQ.Shared.Results;

namespace WireHQ.Application.Common.Behaviors;

/// <summary>
/// Runs every registered <see cref="IValidator{T}"/> for the request before the handler. On
/// failure it short-circuits with a <see cref="ValidationError"/> (→ HTTP 400) — so validation
/// lives with the use case and is enforced no matter how the command is dispatched.
/// (docs/06-api-design.md)
/// </summary>
public sealed class ValidationBehavior<TRequest, TResponse>(IEnumerable<IValidator<TRequest>> validators)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
    where TResponse : Result
{
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        if (!validators.Any())
        {
            return await next();
        }

        var context = new ValidationContext<TRequest>(request);

        var failures = (await Task.WhenAll(
                validators.Select(v => v.ValidateAsync(context, cancellationToken))))
            .SelectMany(r => r.Errors)
            .Where(f => f is not null)
            .ToList();

        if (failures.Count == 0)
        {
            return await next();
        }

        var errors = failures
            .GroupBy(f => Camelize(f.PropertyName), f => f.ErrorMessage)
            .ToDictionary(g => g.Key, g => g.Distinct().ToArray());

        return ResultFactory.Failure<TResponse>(new ValidationError(errors));
    }

    private static string Camelize(string property) =>
        string.IsNullOrEmpty(property) || char.IsLower(property[0])
            ? property
            : char.ToLowerInvariant(property[0]) + property[1..];
}
