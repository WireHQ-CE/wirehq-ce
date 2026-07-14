using MediatR;
using WireHQ.Application.Abstractions.Persistence;
using WireHQ.Application.Common.Messaging;
using WireHQ.Shared.Results;

namespace WireHQ.Application.Common.Behaviors;

/// <summary>
/// The transaction boundary for writes. Command handlers mutate the context but do NOT call
/// SaveChanges themselves — this behavior commits once, after a successful command, so the
/// business change and its audit entry persist atomically. Domain events raised by the touched
/// aggregates are dispatched by the persistence interceptor as part of that save.
/// Queries pass straight through. (docs/02-architecture.md)
/// </summary>
public sealed class UnitOfWorkBehavior<TRequest, TResponse>(IApplicationDbContext dbContext)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
    where TResponse : Result
{
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        if (request is not IBaseCommand)
        {
            return await next();
        }

        var response = await next();

        // Only persist when the use case succeeded; a failed Result leaves the DB untouched.
        if (response.IsSuccess)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return response;
    }
}
