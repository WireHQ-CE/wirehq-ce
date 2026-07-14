using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using WireHQ.Application.Abstractions;
using WireHQ.Application.Features.Webhooks;
using WireHQ.Domain.Auditing;
using WireHQ.Domain.Webhooks;
using WireHQ.Infrastructure.Webhooks;

namespace WireHQ.Infrastructure.Persistence.Interceptors;

/// <summary>
/// The <b>reliable webhook outbox</b> (docs/26-api-keys-webhooks.md §8, K-5). On save it inspects the newly-added
/// <see cref="AuditLog"/> rows and, for each org-scoped entry that matches an <b>active</b> endpoint's subscription
/// (looked up in the in-memory <see cref="WebhookSubscriptionCache"/> — no query on the save path), adds a
/// <c>Pending</c> <see cref="WebhookDelivery"/> to the <b>same unit of work</b>. Because the delivery commits in the
/// same transaction as its cause, it can never be lost or fire before the action commits — the true-outbox property
/// a fire-and-forget dispatcher can't give. Kept-core; a no-op until an endpoint exists (empty cache → nothing added).
/// </summary>
public sealed class WebhookOutboxInterceptor(
    WebhookSubscriptionCache cache, IDateTimeProvider clock, ILogger<WebhookOutboxInterceptor> logger) : SaveChangesInterceptor
{
    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (eventData.Context is { } context)
        {
            await CaptureAsync(context, cancellationToken);
        }

        return await base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private async Task CaptureAsync(DbContext context, CancellationToken cancellationToken)
    {
        // Only org-scoped audit entries can drive a webhook (endpoints are per-org). Platform/anonymous entries
        // (null org) are skipped. Both outcomes flow — a subscriber may care about failures too.
        var audits = context.ChangeTracker.Entries<AuditLog>()
            .Where(e => e.State == EntityState.Added && e.Entity.OrganizationId is not null)
            .Select(e => e.Entity)
            .ToList();

        if (audits.Count == 0)
        {
            return;
        }

        try
        {
            await cache.EnsureLoadedAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The cache's first load opens a second connection; under a cold-start write storm that can contend for
            // the pool. Never fail the *unrelated* business save for a webhook — skip capture this once (the cache
            // warms on the sender's next tick). Reliability trade-off: an event in this narrow window isn't captured.
            logger.LogWarning(ex, "Webhook outbox: subscription cache unavailable; skipping capture for this save.");
            return;
        }

        var now = clock.UtcNow;
        List<WebhookDelivery>? deliveries = null;
        foreach (var audit in audits)
        {
            var organizationId = audit.OrganizationId!.Value;
            var endpoints = cache.MatchingEndpoints(organizationId, audit.Action);
            if (endpoints.Count == 0)
            {
                continue;
            }

            var payload = WebhookPayload.Serialize(audit);
            foreach (var endpointId in endpoints)
            {
                (deliveries ??= []).Add(WebhookDelivery.Create(organizationId, endpointId, audit.Action, payload, now));
            }
        }

        if (deliveries is not null)
        {
            // Added to the same context mid-save → EF includes them in this transaction (the outbox guarantee).
            context.Set<WebhookDelivery>().AddRange(deliveries);
        }
    }
}
