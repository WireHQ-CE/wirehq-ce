using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WireHQ.Application.Abstractions;
using WireHQ.Application.Abstractions.Persistence;
using WireHQ.Application.Abstractions.Security;
using WireHQ.Application.Abstractions.Webhooks;
using WireHQ.Application.Entitlements;
using WireHQ.Domain.Webhooks;

namespace WireHQ.Application.Features.Webhooks;

/// <summary>
/// Drains the webhook outbox (docs/26-api-keys-webhooks.md §8): each tick it sweeps <b>due</b> <c>Pending</c>
/// deliveries <b>cross-tenant</b> in a bypass scope (the scheduled-sweep pattern the other background workers use),
/// POSTs each signed body via <see cref="IWebhookTransport"/>, and marks it <c>Delivered</c> (2xx) or reschedules
/// with exponential backoff up to
/// <see cref="WebhookDelivery.MaxAttempts"/>, then <c>Failed</c>. Also prunes old terminal history so the outbox
/// stays bounded. Driven by the Api host's <c>WebhookSenderHostedService</c>; also invoked directly by tests. Singleton.
/// </summary>
public sealed class WebhookDispatchScheduler(IServiceScopeFactory scopeFactory, ILogger<WebhookDispatchScheduler> logger)
{
    private const int BatchSize = 100;
    private static readonly TimeSpan HistoryRetention = TimeSpan.FromDays(30);

    public async Task RunDueAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        scope.ServiceProvider.GetRequiredService<ISettableTenantContext>().SetBypass();
        var dbContext = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
        var transport = scope.ServiceProvider.GetRequiredService<IWebhookTransport>();
        var protector = scope.ServiceProvider.GetRequiredService<ISecretProtector>();
        var effectiveFeatures = scope.ServiceProvider.GetRequiredService<IEffectiveFeatureSet>();
        var now = scope.ServiceProvider.GetRequiredService<IDateTimeProvider>().UtcNow;

        var due = await dbContext.WebhookDeliveries
            .IgnoreQueryFilters()
            .Where(d => d.Status == WebhookDeliveryStatus.Pending && d.NextAttemptAtUtc <= now)
            .OrderBy(d => d.NextAttemptAtUtc)
            .Take(BatchSize)
            .ToListAsync(cancellationToken);

        if (due.Count > 0)
        {
            // The active endpoints for this batch (url + secret + owning org). A delivery whose endpoint is disabled or
            // deleted is left Pending (re-enabling the endpoint resumes it; a deleted endpoint cascade-deletes its rows).
            var endpointIds = due.Select(d => d.EndpointId).Distinct().ToList();
            var endpoints = await dbContext.WebhookEndpoints
                .IgnoreQueryFilters()
                .Where(e => endpointIds.Contains(e.Id) && e.Status == WebhookEndpointStatus.Active)
                .Select(e => new { e.Id, e.Url, e.SigningSecretCiphertext, e.OrganizationId })
                .ToDictionaryAsync(e => e.Id, cancellationToken);

            // MM-14 data-plane deactivation guard (docs/33 §5.4): the whole webhook control plane is gated on
            // `api.keys` (every command/query declares it), but this bypass drain never re-checked it — so an org that
            // deactivated the api-extensions module (self-host) or downgraded its plan (SaaS) kept getting webhooks for
            // every still-Active endpoint. Re-check the live entitlement union per delivery at SEND time, resolving each
            // org's edition once (the union depends only on edition — the activated-module set is install-global).
            var entitlements = new BackgroundEntitlementResolver(dbContext, effectiveFeatures);
            await entitlements.LoadEditionsAsync(endpoints.Values.Select(e => e.OrganizationId), cancellationToken);

            foreach (var delivery in due)
            {
                if (!endpoints.TryGetValue(delivery.EndpointId, out var endpoint))
                {
                    // The endpoint was disabled or removed after this delivery was queued — abandon it (terminal), so
                    // it can't clog every sweep or leak unbounded. (A deleted endpoint's deliveries are removed by the
                    // delete handler; this also mops up any disabled-endpoint backlog and the rare capture/delete race.)
                    delivery.Cancel("Endpoint disabled or removed", now);
                    continue;
                }

                if (!await entitlements.IsEntitledAsync(endpoint.OrganizationId, PlanFeatures.ApiKeys, cancellationToken))
                {
                    // The org no longer holds the api.keys entitlement (module deactivated / plan downgraded). Stop
                    // delivering queued events for it — terminal, mirroring the Notifications MM-14 guard.
                    delivery.Cancel("api.keys entitlement is no longer active for this organisation", now);
                    continue;
                }

                string secret;
                try
                {
                    secret = protector.Unprotect(endpoint.SigningSecretCiphertext);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Webhook delivery {DeliveryId}: could not unprotect the signing secret.", delivery.Id);
                    delivery.MarkFailed(null, "Signing secret unavailable", now);
                    continue;
                }

                var result = await transport.SendAsync(
                    new WebhookSendRequest(endpoint.Url, delivery.PayloadJson, secret, delivery.Id, delivery.EventType),
                    cancellationToken);

                if (result.Success)
                {
                    delivery.MarkSucceeded(result.StatusCode ?? 200, now);
                }
                else
                {
                    delivery.MarkFailed(result.StatusCode, result.Error, now);
                }
            }

            await dbContext.SaveChangesAsync(cancellationToken);
        }

        // Prune terminal history older than the retention window so the outbox table stays bounded.
        await dbContext.WebhookDeliveries
            .IgnoreQueryFilters()
            .Where(d => (d.Status == WebhookDeliveryStatus.Delivered || d.Status == WebhookDeliveryStatus.Failed)
                        && d.CreatedAtUtc < now - HistoryRetention)
            .ExecuteDeleteAsync(cancellationToken);
    }
}
