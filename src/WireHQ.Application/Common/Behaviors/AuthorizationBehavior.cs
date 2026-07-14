using MediatR;
using WireHQ.Application.Abstractions;
using WireHQ.Application.Common.Messaging;
using WireHQ.Shared.Results;

namespace WireHQ.Application.Common.Behaviors;

/// <summary>
/// Enforces declarative authorization on any request implementing <see cref="IAuthorizedRequest"/>.
/// The caller must be authenticated and hold ALL of the request's required permissions, resolved
/// from their roles in the active organization. Guarding the use case (not just the endpoint)
/// means a command is safe however it is dispatched. (docs/04-security.md)
/// </summary>
public sealed class AuthorizationBehavior<TRequest, TResponse>(ICurrentUser currentUser)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
    where TResponse : Result
{
    private static readonly Error NotAuthenticated =
        Error.Unauthorized("auth.unauthenticated", "Authentication is required.");

    private static readonly Error NotPlatformAdmin =
        Error.Forbidden("auth.platform_required", "Platform operator access is required.");

    private static readonly Error NotPlatformOperator =
        Error.Forbidden("auth.platform_required", "Platform operator access (Support or above) is required.");

    public Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        // Platform tier: above org roles, not org-scoped. Checked first so platform endpoints never
        // depend on an active org/membership. A full IPlatformRequest demands Super Admin (it may mutate);
        // an IPlatformReadRequest is a read-mostly diagnostic open to the lower Support tier as well.
        if (request is IPlatformRequest)
        {
            if (!currentUser.IsAuthenticated)
            {
                return Task.FromResult(ResultFactory.Failure<TResponse>(NotAuthenticated));
            }

            if (!currentUser.IsPlatformAdmin)
            {
                return Task.FromResult(ResultFactory.Failure<TResponse>(NotPlatformAdmin));
            }
        }
        else if (request is IPlatformReadRequest)
        {
            if (!currentUser.IsAuthenticated)
            {
                return Task.FromResult(ResultFactory.Failure<TResponse>(NotAuthenticated));
            }

            if (!currentUser.IsPlatformOperator)
            {
                return Task.FromResult(ResultFactory.Failure<TResponse>(NotPlatformOperator));
            }
        }

        if (request is not IAuthorizedRequest authorized)
        {
            return next();
        }

        if (!currentUser.IsAuthenticated)
        {
            return Task.FromResult(ResultFactory.Failure<TResponse>(NotAuthenticated));
        }

        var missing = authorized.RequiredPermissions
            .Where(permission => !currentUser.HasPermission(permission))
            .ToArray();

        if (missing.Length > 0)
        {
            var error = Error.Forbidden(
                "auth.insufficient_permission",
                $"Requires permission: {string.Join(", ", missing)}.");

            return Task.FromResult(ResultFactory.Failure<TResponse>(error));
        }

        return next();
    }
}
