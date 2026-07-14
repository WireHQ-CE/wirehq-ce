using Microsoft.EntityFrameworkCore;
using WireHQ.Domain.Webhooks;

namespace WireHQ.Application.Abstractions.Persistence;

/// <summary>
/// The webhooks slice of the persistence port (docs/26-api-keys-webhooks.md §7-8). Endpoints + the delivery outbox.
/// Kept-core — webhooks are an entitlement-gated platform capability (<c>api.keys</c>), not a SaaS-only module, so
/// this ships in <b>every</b> edition (the CE defaults orgs to Enterprise). Tenant-owned in the reused
/// <c>identity</c> schema (RLS for free — both tables carry <c>organization_id</c>).
/// </summary>
public partial interface IApplicationDbContext
{
    DbSet<WebhookEndpoint> WebhookEndpoints { get; }

    DbSet<WebhookDelivery> WebhookDeliveries { get; }
}
