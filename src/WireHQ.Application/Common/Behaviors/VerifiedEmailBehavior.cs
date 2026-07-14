using MediatR;
using Microsoft.EntityFrameworkCore;
using WireHQ.Application.Abstractions;
using WireHQ.Application.Abstractions.Persistence;
using WireHQ.Application.Common.Messaging;
using WireHQ.Shared.Results;

namespace WireHQ.Application.Common.Behaviors;

/// <summary>
/// Soft email-verification gate. A request marked <see cref="IRequiresVerifiedEmail"/> only runs once the
/// caller's email is confirmed — so unverified users can still sign in and complete onboarding, but
/// sensitive actions (creating VPN config, inviting members, …) are blocked until they verify. Runs after
/// <see cref="AuthorizationBehavior{TRequest,TResponse}"/>. (docs/04-security.md)
/// </summary>
public sealed class VerifiedEmailBehavior<TRequest, TResponse>(ICurrentUser currentUser, IApplicationDbContext dbContext)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
    where TResponse : Result
{
    private static readonly Error NotAuthenticated =
        Error.Unauthorized("auth.unauthenticated", "Authentication is required.");

    private static readonly Error EmailUnverified =
        Error.Forbidden("auth.email_unverified", "Please verify your email address first — check your inbox for the confirmation link.");

    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        if (request is not IRequiresVerifiedEmail)
        {
            return await next();
        }

        if (!currentUser.IsAuthenticated || currentUser.UserId is not { } userId)
        {
            return ResultFactory.Failure<TResponse>(NotAuthenticated);
        }

        var verified = await dbContext.Users
            .IgnoreQueryFilters()
            .Where(u => u.Id == userId)
            .Select(u => u.EmailVerified)
            .FirstOrDefaultAsync(cancellationToken);

        return verified ? await next() : ResultFactory.Failure<TResponse>(EmailUnverified);
    }
}
