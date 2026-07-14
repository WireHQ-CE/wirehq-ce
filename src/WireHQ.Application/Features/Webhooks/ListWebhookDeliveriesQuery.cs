using Microsoft.EntityFrameworkCore;
using WireHQ.Application.Abstractions.Persistence;
using WireHQ.Application.Authorization;
using WireHQ.Application.Common.Messaging;
using WireHQ.Application.Entitlements;
using WireHQ.Shared.Results;

namespace WireHQ.Application.Features.Webhooks;

/// <summary>
/// The recent delivery history — the customer-visible outbox (docs/26-api-keys-webhooks.md §8), optionally scoped to
/// one endpoint. Tenant-filtered (each delivery carries the org). Enterprise-gated (api.keys) + api.keys.manage.
/// </summary>
public sealed record ListWebhookDeliveriesQuery(Guid? EndpointId) : IQuery<IReadOnlyList<WebhookDeliveryItem>>, IAuthorizedRequest, IRequiresFeature
{
    private const int MaxResults = 100;

    public int Take => MaxResults;

    public IReadOnlyCollection<string> RequiredPermissions => [Permissions.ApiKeys.Manage];

    public string RequiredFeature => PlanFeatures.ApiKeys;
}

public sealed record WebhookDeliveryItem(
    Guid Id,
    Guid EndpointId,
    string EventType,
    string Status,
    int Attempts,
    int? LastResponseCode,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? DeliveredAtUtc,
    DateTimeOffset? NextAttemptAtUtc);

public sealed class ListWebhookDeliveriesQueryHandler(IApplicationDbContext dbContext)
    : IQueryHandler<ListWebhookDeliveriesQuery, IReadOnlyList<WebhookDeliveryItem>>
{
    public async Task<Result<IReadOnlyList<WebhookDeliveryItem>>> Handle(ListWebhookDeliveriesQuery query, CancellationToken cancellationToken)
    {
        var deliveries = await dbContext.WebhookDeliveries
            .AsNoTracking()
            .Where(d => query.EndpointId == null || d.EndpointId == query.EndpointId)
            .OrderByDescending(d => d.CreatedAtUtc)
            .Take(query.Take)
            .Select(d => new WebhookDeliveryItem(
                d.Id,
                d.EndpointId,
                d.EventType,
                d.Status.ToString(),
                d.Attempts,
                d.LastResponseCode,
                d.CreatedAtUtc,
                d.DeliveredAtUtc,
                d.NextAttemptAtUtc))
            .ToListAsync(cancellationToken);

        return Result.Success<IReadOnlyList<WebhookDeliveryItem>>(deliveries);
    }
}
