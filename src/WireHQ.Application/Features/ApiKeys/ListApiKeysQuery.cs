using Microsoft.EntityFrameworkCore;
using WireHQ.Application.Abstractions;
using WireHQ.Application.Abstractions.Persistence;
using WireHQ.Application.Authorization;
using WireHQ.Application.Common.Messaging;
using WireHQ.Application.Entitlements;
using WireHQ.Domain.ApiKeys;
using WireHQ.Shared.Results;

namespace WireHQ.Application.Features.ApiKeys;

/// <summary>
/// Lists the active organization's API keys for the management console (docs/26-api-keys-webhooks.md §6). The
/// secret is never projected — only the display prefix + metadata. Enterprise-gated (api.keys) + api.keys.manage.
/// </summary>
public sealed record ListApiKeysQuery : IQuery<IReadOnlyList<ApiKeyListItem>>, IAuthorizedRequest, IRequiresFeature
{
    public IReadOnlyCollection<string> RequiredPermissions => [Permissions.ApiKeys.Manage];

    public string RequiredFeature => PlanFeatures.ApiKeys;
}

public sealed record ApiKeyListItem(
    Guid Id,
    string Name,
    string KeyPrefix,
    IReadOnlyList<string> Scopes,
    // Effective status for the console — Active, Expired (past its expiry), or Revoked; folds in expiry (see the handler).
    string Status,
    DateTimeOffset? ExpiresAtUtc,
    DateTimeOffset? LastUsedAtUtc,
    DateTimeOffset CreatedAtUtc);

public sealed class ListApiKeysQueryHandler(IApplicationDbContext dbContext, IDateTimeProvider clock)
    : IQueryHandler<ListApiKeysQuery, IReadOnlyList<ApiKeyListItem>>
{
    public async Task<Result<IReadOnlyList<ApiKeyListItem>>> Handle(ListApiKeysQuery query, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;

        var keys = await dbContext.ApiKeys
            .AsNoTracking()
            .OrderByDescending(k => k.CreatedAtUtc)
            .Select(k => new ApiKeyListItem(
                k.Id,
                k.Name,
                k.KeyPrefix,
                k.Scopes.Select(s => s.PermissionKey).ToList(),
                // Effective status: expiry isn't stored on Status (revoke is a hard delete), so fold it in here so
                // an expired-but-not-deleted key reads "Expired", not a misleading "Active". (review polish)
                k.Status == ApiKeyStatus.Revoked ? nameof(ApiKeyStatus.Revoked)
                    : k.ExpiresAtUtc != null && k.ExpiresAtUtc <= now ? "Expired"
                    : nameof(ApiKeyStatus.Active),
                k.ExpiresAtUtc,
                k.LastUsedAtUtc,
                k.CreatedAtUtc))
            .ToListAsync(cancellationToken);

        return Result.Success<IReadOnlyList<ApiKeyListItem>>(keys);
    }
}
