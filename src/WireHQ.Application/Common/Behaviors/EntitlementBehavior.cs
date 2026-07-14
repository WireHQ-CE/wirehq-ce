using MediatR;
using WireHQ.Application.Common.Messaging;
using WireHQ.Application.Entitlements;
using WireHQ.Shared.Results;

namespace WireHQ.Application.Common.Behaviors;

/// <summary>
/// Plan/feature gate. A request marked <see cref="IRequiresFeature"/> only runs when the active
/// organisation's plan (edition) includes that feature — otherwise it's blocked with
/// <c>plan.upgrade_required</c> (RFC 9457), independent of how it's dispatched. Runs after
/// <see cref="VerifiedEmailBehavior{TRequest,TResponse}"/>. Quotas (counts) are enforced in the handlers
/// via <see cref="IEntitlementService.EnsureCanAddAsync"/>. (docs/commercial.md §6)
/// </summary>
public sealed class EntitlementBehavior<TRequest, TResponse>(IEntitlementService entitlements)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
    where TResponse : Result
{
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        if (request is not IRequiresFeature gated)
        {
            return await next();
        }

        if (await entitlements.HasFeatureAsync(gated.RequiredFeature, cancellationToken))
        {
            return await next();
        }

        return ResultFactory.Failure<TResponse>(Error.Forbidden(
            "plan.upgrade_required", "Your current plan does not include this feature. Upgrade your plan to use it."));
    }
}
