using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WireHQ.Domain.ApiKeys;

namespace WireHQ.Infrastructure.Persistence.Configurations;

// API-key entity configurations (docs/26-api-keys-webhooks.md §4, ADR-043). Tenant-owned (organization_id) in the
// reused `identity` schema, so rls.sql's data-driven tenant_isolation + wirehq_app grants cover them — no rls.sql
// change. Kept-core (entitlement-gated, not CE-stripped). Index names are explicit (EF derives them from
// table+column, ignoring the schema). Scopes are a NORMAL child entity (not owned) so replacing a key is an
// INSERT/DELETE, dodging the owned-collection append gotcha (the RolePermission lesson).

public sealed class ApiKeyConfiguration : IEntityTypeConfiguration<ApiKey>
{
    public void Configure(EntityTypeBuilder<ApiKey> builder)
    {
        builder.ToTable("api_keys", "identity");
        builder.HasKey(k => k.Id);
        builder.Property(k => k.Id).ValueGeneratedNever();
        builder.Ignore(k => k.DomainEvents);

        builder.Property(k => k.OrganizationId).IsRequired();
        builder.Property(k => k.Name).HasMaxLength(ApiKey.MaxNameLength).IsRequired();
        builder.Property(k => k.KeyPrefix).HasMaxLength(32).IsRequired();
        builder.Property(k => k.KeyHash).HasMaxLength(128).IsRequired();
        builder.Property(k => k.Status).HasConversion<string>().HasMaxLength(16).IsRequired();

        builder.HasMany(k => k.Scopes)
            .WithOne()
            .HasForeignKey(s => s.ApiKeyId)
            .OnDelete(DeleteBehavior.Cascade);

        // The authentication scheme looks a key up by its hash — globally unique + indexed.
        builder.HasIndex(k => k.KeyHash).IsUnique().HasDatabaseName("ix_api_keys_key_hash");
        builder.HasIndex(k => k.OrganizationId).HasDatabaseName("ix_api_keys_organization_id");
    }
}

public sealed class ApiKeyScopeConfiguration : IEntityTypeConfiguration<ApiKeyScope>
{
    public void Configure(EntityTypeBuilder<ApiKeyScope> builder)
    {
        builder.ToTable("api_key_scopes", "identity");
        builder.HasKey(s => new { s.ApiKeyId, s.PermissionKey });
        builder.Property(s => s.PermissionKey).HasMaxLength(128).IsRequired();
    }
}
