using WireHQ.Application.Authorization;
using WireHQ.Application.Common.Messaging;
using WireHQ.Application.Entitlements;
using WireHQ.Shared.Results;

namespace WireHQ.Application.Features.ApiKeys;

/// <summary>
/// The grantable-scope catalog for the API-key editor (docs/26-api-keys-webhooks.md §6) — the permission keys a
/// key can be scoped to, grouped for the picker. The UI further restricts to what the actor holds; the create
/// command enforces it server-side. Enterprise-gated (api.keys) + api.keys.manage.
/// </summary>
public sealed record ListApiKeyScopesQuery : IQuery<IReadOnlyList<ApiKeyScopeOption>>, IAuthorizedRequest, IRequiresFeature
{
    public IReadOnlyCollection<string> RequiredPermissions => [Permissions.ApiKeys.Manage];

    public string RequiredFeature => PlanFeatures.ApiKeys;
}

public sealed record ApiKeyScopeOption(string Key, string Group, string Description);

public sealed class ListApiKeyScopesQueryHandler : IQueryHandler<ListApiKeyScopesQuery, IReadOnlyList<ApiKeyScopeOption>>
{
    public Task<Result<IReadOnlyList<ApiKeyScopeOption>>> Handle(ListApiKeyScopesQuery query, CancellationToken cancellationToken)
    {
        IReadOnlyList<ApiKeyScopeOption> scopes = Permissions.All
            .Select(p => new ApiKeyScopeOption(p.Key, p.Group, p.Description))
            .ToList();

        return Task.FromResult(Result.Success(scopes));
    }
}
