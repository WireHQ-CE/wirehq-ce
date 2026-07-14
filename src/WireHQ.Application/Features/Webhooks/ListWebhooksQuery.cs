using Microsoft.EntityFrameworkCore;
using WireHQ.Application.Abstractions.Persistence;
using WireHQ.Application.Authorization;
using WireHQ.Application.Common.Messaging;
using WireHQ.Application.Entitlements;
using WireHQ.Shared.Results;

namespace WireHQ.Application.Features.Webhooks;

/// <summary>
/// Lists the active organization's webhook endpoints for the console (docs/26-api-keys-webhooks.md §8). The signing
/// secret is never projected. Enterprise-gated (api.keys) + api.keys.manage.
/// </summary>
public sealed record ListWebhooksQuery : IQuery<IReadOnlyList<WebhookListItem>>, IAuthorizedRequest, IRequiresFeature
{
    public IReadOnlyCollection<string> RequiredPermissions => [Permissions.ApiKeys.Manage];

    public string RequiredFeature => PlanFeatures.ApiKeys;
}

public sealed record WebhookListItem(
    Guid Id,
    string Url,
    string? Description,
    IReadOnlyList<string> EventTypes,
    string Status,
    DateTimeOffset CreatedAtUtc);

public sealed class ListWebhooksQueryHandler(IApplicationDbContext dbContext)
    : IQueryHandler<ListWebhooksQuery, IReadOnlyList<WebhookListItem>>
{
    public async Task<Result<IReadOnlyList<WebhookListItem>>> Handle(ListWebhooksQuery query, CancellationToken cancellationToken)
    {
        var endpoints = await dbContext.WebhookEndpoints
            .AsNoTracking()
            .OrderByDescending(e => e.CreatedAtUtc)
            .Select(e => new WebhookListItem(
                e.Id,
                e.Url,
                e.Description,
                e.EventTypes.Select(s => s.Pattern).ToList(),
                e.Status.ToString(),
                e.CreatedAtUtc))
            .ToListAsync(cancellationToken);

        return Result.Success<IReadOnlyList<WebhookListItem>>(endpoints);
    }
}
