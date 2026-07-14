using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WireHQ.Domain.Notifications;

namespace WireHQ.Infrastructure.Persistence.Configurations;

// Notification entity configurations (docs/35-notifications.md §4.2). Tenant-owned (organization_id) in the reused
// `identity` schema, so rls.sql's data-driven tenant_isolation + wirehq_app grants cover every table — no rls.sql
// change. Kept-core (the spine ships in every edition). Index names are explicit (EF derives them from table+column,
// ignoring the schema). NB: notification_deliveries.dedup_value is deliberately NOT unique — a uniqueness check on a
// row the interceptor path can reach would risk failing the business transaction (docs/35 B-5).

public sealed class NotificationRuleConfiguration : IEntityTypeConfiguration<NotificationRule>
{
    public void Configure(EntityTypeBuilder<NotificationRule> builder)
    {
        builder.ToTable("notification_rules", "identity");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).ValueGeneratedNever();
        builder.Ignore(r => r.DomainEvents);

        builder.Property(r => r.OrganizationId).IsRequired();
        builder.Property(r => r.Name).HasMaxLength(NotificationRule.MaxNameLength).IsRequired();
        builder.Property(r => r.EventPattern).HasMaxLength(NotificationRule.MaxPatternLength).IsRequired();
        builder.Property(r => r.ChannelKind).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(r => r.Audience).HasConversion<string>().HasMaxLength(24).IsRequired();
        builder.Property(r => r.RequiredFeature).HasMaxLength(128);
        builder.Property(r => r.Enabled).IsRequired();

        builder.HasIndex(r => r.OrganizationId).HasDatabaseName("ix_notification_rules_organization_id");
    }
}

public sealed class NotificationJobConfiguration : IEntityTypeConfiguration<NotificationJob>
{
    public void Configure(EntityTypeBuilder<NotificationJob> builder)
    {
        builder.ToTable("notification_jobs", "identity");
        builder.HasKey(j => j.Id);
        builder.Property(j => j.Id).ValueGeneratedNever();

        builder.Property(j => j.OrganizationId).IsRequired();
        builder.Property(j => j.RuleId).IsRequired();
        builder.Property(j => j.Action).HasMaxLength(NotificationJob.MaxActionLength).IsRequired();
        builder.Property(j => j.SummarySnapshot).HasMaxLength(NotificationJob.MaxSummaryLength).IsRequired();
        builder.Property(j => j.Status).HasConversion<string>().HasMaxLength(16).IsRequired();

        // The expand sweep selects Pending jobs across all tenants (in a bypass scope).
        builder.HasIndex(j => j.Status).HasDatabaseName("ix_notification_jobs_status");
    }
}

public sealed class NotificationDeliveryConfiguration : IEntityTypeConfiguration<NotificationDelivery>
{
    public void Configure(EntityTypeBuilder<NotificationDelivery> builder)
    {
        builder.ToTable("notification_deliveries", "identity");
        builder.HasKey(d => d.Id);
        builder.Property(d => d.Id).ValueGeneratedNever();

        builder.Property(d => d.OrganizationId).IsRequired();
        builder.Property(d => d.RuleId).IsRequired();
        builder.Property(d => d.JobId).IsRequired();
        builder.Property(d => d.ChannelKind).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(d => d.RequiredFeature).HasMaxLength(128);
        builder.Property(d => d.Recipient).HasMaxLength(NotificationDelivery.MaxRecipientLength).IsRequired();
        builder.Property(d => d.RenderedSubject).HasMaxLength(NotificationDelivery.MaxSubjectLength).IsRequired();
        builder.Property(d => d.RenderedBody).IsRequired();
        builder.Property(d => d.DedupValue).HasMaxLength(64);
        builder.Property(d => d.Status).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(d => d.Attempts).IsRequired();
        builder.Property(d => d.LastError).HasMaxLength(NotificationDelivery.MaxErrorLength);

        // RuleId/JobId are SOFT references (no FK) — the delete handler removes a rule's rows explicitly; a hard FK
        // could fail an unrelated business transaction (the webhook precedent, docs/35 §4.2).
        builder.HasIndex(d => new { d.Status, d.NextAttemptAtUtc }).HasDatabaseName("ix_notification_deliveries_status_next_attempt");
        builder.HasIndex(d => d.RuleId).HasDatabaseName("ix_notification_deliveries_rule_id");
    }
}

public sealed class NotificationChannelConfigConfiguration : IEntityTypeConfiguration<NotificationChannelConfig>
{
    public void Configure(EntityTypeBuilder<NotificationChannelConfig> builder)
    {
        builder.ToTable("notification_channel_configs", "identity");
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).ValueGeneratedNever();
        builder.Ignore(c => c.DomainEvents);

        builder.Property(c => c.OrganizationId).IsRequired();
        builder.Property(c => c.ChannelKind).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(c => c.ProviderKind).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(c => c.DestinationUrl).HasMaxLength(NotificationChannelConfig.MaxUrlLength);
        builder.Property(c => c.CredentialCiphertext).HasMaxLength(1024);
        builder.Property(c => c.FromValue).HasMaxLength(NotificationChannelConfig.MaxFromLength);
        builder.Property(c => c.Enabled).IsRequired();

        builder.HasIndex(c => new { c.OrganizationId, c.ChannelKind })
            .IsUnique()
            .HasDatabaseName("ux_notification_channel_configs_org_channel");
    }
}

public sealed class NotificationChannelUsageConfiguration : IEntityTypeConfiguration<NotificationChannelUsage>
{
    public void Configure(EntityTypeBuilder<NotificationChannelUsage> builder)
    {
        builder.ToTable("notification_channel_usage", "identity");
        builder.HasKey(u => u.Id);
        builder.Property(u => u.Id).ValueGeneratedNever();

        builder.Property(u => u.OrganizationId).IsRequired();
        builder.Property(u => u.ChannelKind).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(u => u.DayUtc).IsRequired();
        builder.Property(u => u.Count).IsRequired();

        builder.HasIndex(u => new { u.OrganizationId, u.ChannelKind, u.DayUtc })
            .IsUnique()
            .HasDatabaseName("ux_notification_channel_usage_org_channel_day");
    }
}
