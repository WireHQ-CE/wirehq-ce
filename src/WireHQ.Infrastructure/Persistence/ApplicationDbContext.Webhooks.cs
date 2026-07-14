using Microsoft.EntityFrameworkCore;
using WireHQ.Domain.Webhooks;

namespace WireHQ.Infrastructure.Persistence;

/// <summary>The webhooks slice of the concrete context (docs/26-api-keys-webhooks.md §7-8). Kept-core — ships in
/// every edition (entitlement-gated, not stripped).</summary>
public sealed partial class ApplicationDbContext
{
    public DbSet<WebhookEndpoint> WebhookEndpoints => Set<WebhookEndpoint>();

    public DbSet<WebhookDelivery> WebhookDeliveries => Set<WebhookDelivery>();
}
