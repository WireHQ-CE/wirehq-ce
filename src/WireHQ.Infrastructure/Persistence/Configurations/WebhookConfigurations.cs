using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WireHQ.Domain.Webhooks;

namespace WireHQ.Infrastructure.Persistence.Configurations;

// Webhook entity configurations (docs/26-api-keys-webhooks.md §7-8, ADR-043). Tenant-owned (organization_id) in the
// reused `identity` schema, so rls.sql's data-driven tenant_isolation + wirehq_app grants cover BOTH tables — no
// rls.sql change (the delivery carries organization_id precisely so RLS covers it). Kept-core (entitlement-gated,
// not CE-stripped). Event subscriptions are a NORMAL child entity (the ApiKeyScope/RolePermission lesson). Index
// names are explicit (EF derives them from table+column, ignoring the schema).

public sealed class WebhookEndpointConfiguration : IEntityTypeConfiguration<WebhookEndpoint>
{
    public void Configure(EntityTypeBuilder<WebhookEndpoint> builder)
    {
        builder.ToTable("webhook_endpoints", "identity");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedNever();
        builder.Ignore(e => e.DomainEvents);

        builder.Property(e => e.OrganizationId).IsRequired();
        builder.Property(e => e.Url).HasMaxLength(WebhookEndpoint.MaxUrlLength).IsRequired();
        builder.Property(e => e.Description).HasMaxLength(WebhookEndpoint.MaxDescriptionLength);
        builder.Property(e => e.SigningSecretCiphertext).HasMaxLength(1024).IsRequired();
        builder.Property(e => e.Status).HasConversion<string>().HasMaxLength(16).IsRequired();

        builder.HasMany(e => e.EventTypes)
            .WithOne()
            .HasForeignKey(s => s.EndpointId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(e => e.OrganizationId).HasDatabaseName("ix_webhook_endpoints_organization_id");
    }
}

public sealed class WebhookEventSubscriptionConfiguration : IEntityTypeConfiguration<WebhookEventSubscription>
{
    public void Configure(EntityTypeBuilder<WebhookEventSubscription> builder)
    {
        builder.ToTable("webhook_event_subscriptions", "identity");
        builder.HasKey(s => new { s.EndpointId, s.Pattern });
        builder.Property(s => s.Pattern).HasMaxLength(WebhookEndpoint.MaxEventTypeLength).IsRequired();
    }
}

public sealed class WebhookDeliveryConfiguration : IEntityTypeConfiguration<WebhookDelivery>
{
    public void Configure(EntityTypeBuilder<WebhookDelivery> builder)
    {
        builder.ToTable("webhook_deliveries", "identity");
        builder.HasKey(d => d.Id);
        builder.Property(d => d.Id).ValueGeneratedNever();

        builder.Property(d => d.OrganizationId).IsRequired();
        builder.Property(d => d.EndpointId).IsRequired();
        builder.Property(d => d.EventType).HasMaxLength(WebhookEndpoint.MaxEventTypeLength).IsRequired();
        builder.Property(d => d.PayloadJson).IsRequired();
        builder.Property(d => d.Status).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(d => d.Attempts).IsRequired();
        builder.Property(d => d.LastError).HasMaxLength(WebhookDelivery.MaxErrorLength);

        // EndpointId is a SOFT reference (no FK). The outbox interceptor inserts a delivery from the in-memory
        // subscription cache, which can lag an endpoint delete by a tick — a hard FK would make that stale insert
        // fail the *unrelated* business action's transaction. Instead the delete handler removes an endpoint's
        // deliveries explicitly, and the sender abandons a delivery whose endpoint has since gone (docs/26 §8).

        // The background sender drains due Pending rows across all tenants (in a bypass scope).
        builder.HasIndex(d => new { d.Status, d.NextAttemptAtUtc }).HasDatabaseName("ix_webhook_deliveries_status_next_attempt");
        builder.HasIndex(d => d.EndpointId).HasDatabaseName("ix_webhook_deliveries_endpoint_id");
    }
}
