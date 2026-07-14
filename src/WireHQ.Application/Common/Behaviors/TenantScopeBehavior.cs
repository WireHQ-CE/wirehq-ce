using MediatR;
using WireHQ.Application.Abstractions;
using WireHQ.Application.Common.Messaging;
using WireHQ.Shared.Results;

namespace WireHQ.Application.Common.Behaviors;

/// <summary>
/// Opts cross-tenant / pre-org use cases out of the Postgres RLS tenant policy for the rest of the
/// request — set <b>before</b> the handler (or its validators) runs any query, so their legitimate reads
/// aren't blocked by the database. Three kinds qualify: <see cref="ITenantUnscopedRequest"/> (session
/// minting, org provisioning, GetMe), every <see cref="IPlatformRequest"/>, and every
/// <see cref="IPlatformReadRequest"/> (a platform operator acts
/// across tenants by definition — independent of whether they happen to own a personal org, which would
/// otherwise leave them org-scoped). Runs early in the pipeline. Normal org-scoped requests are untouched:
/// RLS stays enforced via the <c>app.current_org</c> GUC. (docs/03-multi-tenancy.md, ADR-027)
/// </summary>
public sealed class TenantScopeBehavior<TRequest, TResponse>(ISettableTenantContext tenant)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
    where TResponse : Result
{
    public Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        if (request is ITenantUnscopedRequest or IPlatformRequest or IPlatformReadRequest)
        {
            tenant.SetBypass();
        }

        return next();
    }
}
