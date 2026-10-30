using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using WireHQ.Domain.Notifications;
using WireHQ.Domain.ValueObjects;

namespace WireHQ.Infrastructure.Persistence.Configurations;

// Notification entity configurations (docs/35-notifications.md §4.2). Tenant-owned (organization_id) in the reused
// `identity` schema, so rls.sql's data-driven tenant_isolation + wirehq_app grants cover every table — no rls.sql
// change. Kept-core (the spine ships in every edition). Index names are explicit (EF derives them from table+column,
// ignoring the schema). NB: notification_deliveries.dedup_value is deliberately NOT unique — a uniqueness check on a
// row the interceptor path can reach would risk failing the business transaction (docs/35 B-5).

/// <summary>Stores a rule/delivery's <c>RequiredFeatures</c> SET (docs/35 Wave 3, set-valued MM-14) as a single
/// comma-delimited, non-null string column. Feature keys are dotted lowercase (<c>notifications.chat</c>) and never
/// contain a comma, so the delimiter is unambiguous; the empty set round-trips as "" (free-core). Comparison is
/// order-independent so the drain's set membership check is stable regardless of stored order.</summary>
internal static class NotificationFeatureSetConverters
{
    private const char Delimiter = ',';

    public static readonly ValueConverter<IReadOnlyCollection<string>, string> Converter =
        new(v => string.Join(Delimiter, v),
            v => string.IsNullOrEmpty(v)
                ? Array.Empty<string>()
                : v.Split(Delimiter, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    public static readonly ValueComparer<IReadOnlyCollection<string>> Comparer =
        new((a, b) => a!.OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(b!.OrderBy(x => x, StringComparer.Ordinal)),
            v => v.OrderBy(x => x, StringComparer.Ordinal).Aggregate(0, (h, s) => HashCode.Combine(h, s.GetHashCode(StringComparison.Ordinal))),
            v => v.ToArray());
}

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
        builder.Property(r => r.RequiredFeatures)
            .HasColumnName("required_features")
            .HasConversion(NotificationFeatureSetConverters.Converter, NotificationFeatureSetConverters.Comparer)
            .HasMaxLength(512)
            .IsRequired()
            .HasDefaultValue(Array.Empty<string>());
        builder.Property(r => r.Enabled).IsRequired();

        // Digests (docs/35 §4.5, Wave 3): the cadence + the next-flush cursor. A rule with a due cursor is picked up by
        // the FlushDigestsAsync drain phase; the index covers that cross-tenant due-scan. Every insert supplies the
        // cadence (the domain defaults it to Immediate); the migration backfills any pre-existing row with 'Immediate'.
        builder.Property(r => r.DigestCadence).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(r => r.NextDigestAtUtc);
        builder.HasIndex(r => r.NextDigestAtUtc).HasDatabaseName("ix_notification_rules_next_digest_at");

        // Quiet hours (docs/35 §5, Wave 3): a per-rule local window during which deliveries are deferred (not dropped)
        // to the window's end. All three nullable together (all-or-none, domain-enforced); TimeOnly maps to Postgres
        // `time`. The migration adds them nullable, so pre-existing rules keep quiet hours off.
        builder.Property(r => r.QuietHoursStart);
        builder.Property(r => r.QuietHoursEnd);
        builder.Property(r => r.QuietHoursTimeZone).HasMaxLength(64);

        // Additional event globs on a multi-pattern (advanced) rule — a NORMAL composite-keyed child (RuleId, Pattern),
        // the WebhookEventSubscription precedent, dodging the owned-collection append gotcha (ef-owned-collection-append).
        // Mapped from the backing field so the aggregate owns the collection.
        builder.HasMany(r => r.AdditionalPatterns)
            .WithOne()
            .HasForeignKey(p => p.RuleId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.Metadata.FindNavigation(nameof(NotificationRule.AdditionalPatterns))!
            .SetPropertyAccessMode(PropertyAccessMode.Field);

        // Escalation chain (docs/35 §5, Wave 3) — surrogate-keyed children replaced wholesale by the aggregate; mapped
        // from the backing field. Cascade-delete with the rule.
        builder.HasMany(r => r.EscalationSteps)
            .WithOne()
            .HasForeignKey(s => s.RuleId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.Metadata.FindNavigation(nameof(NotificationRule.EscalationSteps))!
            .SetPropertyAccessMode(PropertyAccessMode.Field);

        builder.HasIndex(r => r.OrganizationId).HasDatabaseName("ix_notification_rules_organization_id");
    }
}

public sealed class NotificationRulePatternConfiguration : IEntityTypeConfiguration<NotificationRulePattern>
{
    public void Configure(EntityTypeBuilder<NotificationRulePattern> builder)
    {
        builder.ToTable("notification_rule_patterns", "identity");
        builder.HasKey(p => new { p.RuleId, p.Pattern });
        builder.Property(p => p.Pattern).HasMaxLength(NotificationRule.MaxPatternLength).IsRequired();
    }
}

public sealed class NotificationEscalationStepConfiguration : IEntityTypeConfiguration<NotificationEscalationStep>
{
    public void Configure(EntityTypeBuilder<NotificationEscalationStep> builder)
    {
        builder.ToTable("notification_escalation_steps", "identity");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).ValueGeneratedNever();

        builder.Property(s => s.RuleId).IsRequired();
        builder.Property(s => s.StepOrder).IsRequired();
        builder.Property(s => s.DelayMinutes).IsRequired();
        builder.Property(s => s.ChannelKind).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(s => s.Audience).HasConversion<string>().HasMaxLength(24).IsRequired();

        builder.HasIndex(s => s.RuleId).HasDatabaseName("ix_notification_escalation_steps_rule_id");
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

        // Digest cadence stamped at capture (docs/35 §4.5): the immediate-expand sweep selects Pending jobs with
        // DigestCadence == Immediate (excluding digest jobs at the SQL level), so the (status, cadence) index covers it.
        // Every insert supplies the cadence; the migration backfills any pre-existing row with 'Immediate'.
        builder.Property(j => j.DigestCadence).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.HasIndex(j => new { j.Status, j.DigestCadence }).HasDatabaseName("ix_notification_jobs_status_digest_cadence");
        // FlushDigestsAsync gathers a rule's pending digest jobs by RuleId.
        builder.HasIndex(j => j.RuleId).HasDatabaseName("ix_notification_jobs_rule_id");

        // Escalation state (docs/35 §5, Wave 3). The drain's EscalateAsync selects Escalating jobs whose cursor is due;
        // the (status, next-due) index covers that cross-tenant due-scan without a rule join (EscalationStepCount is
        // denormalised). The migration defaults EscalationLevel/StepCount to 0 for pre-existing rows.
        builder.Property(j => j.EscalationLevel).IsRequired();
        builder.Property(j => j.EscalationStepCount).IsRequired();
        builder.Property(j => j.EscalationNextDueAtUtc);
        builder.Property(j => j.AcknowledgedAtUtc);
        builder.Property(j => j.AcknowledgedBy);
        builder.HasIndex(j => new { j.Status, j.EscalationNextDueAtUtc }).HasDatabaseName("ix_notification_jobs_status_escalation_due");
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
        builder.Property(d => d.RequiredFeatures)
            .HasColumnName("required_features")
            .HasConversion(NotificationFeatureSetConverters.Converter, NotificationFeatureSetConverters.Comparer)
            .HasMaxLength(512)
            .IsRequired()
            .HasDefaultValue(Array.Empty<string>());
        builder.Property(d => d.Recipient).HasMaxLength(NotificationDelivery.MaxRecipientLength).IsRequired();
        builder.Property(d => d.RenderedSubject).HasMaxLength(NotificationDelivery.MaxSubjectLength).IsRequired();
        builder.Property(d => d.RenderedBody).IsRequired();
        builder.Property(d => d.DedupValue).HasMaxLength(64);
        // varchar(16) still fits every member — "Unresolved" (W4) is 10 characters, so the new state needs no widening.
        builder.Property(d => d.Status).HasConversion<string>().HasMaxLength(16).IsRequired();
        // The provider's message id (Twilio SM… SID), recorded on send or on the status callback (W4).
        builder.Property(d => d.ProviderMessageId).HasMaxLength(NotificationDelivery.MaxProviderMessageIdLength);
        builder.Property(d => d.Attempts).IsRequired();
        builder.Property(d => d.LastError).HasMaxLength(NotificationDelivery.MaxErrorLength);

        // Quiet-hours window copied from the rule at expand (docs/35 §5) so the send path defers without a rule load.
        builder.Property(d => d.QuietHoursStart);
        builder.Property(d => d.QuietHoursEnd);
        builder.Property(d => d.QuietHoursTimeZone).HasMaxLength(64);
        // Escalation level (docs/35 §5): 0 = primary, N = escalation step N. The migration defaults it to 0.
        builder.Property(d => d.EscalationLevel).IsRequired();

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
        // SHA-256 hex (64 chars) of the inbound webhook token (Wave 4 Slice D); nullable until minted. The migration
        // adds it nullable, so pre-existing SMS/chat configs are unaffected.
        builder.Property(c => c.InboundWebhookTokenHash).HasMaxLength(128);
        // A2P/10DLC attestation (Wave 4 Slice D §4.6); all nullable — the migration adds them so existing rows are
        // un-attested (which the pre-send gate treats as "hold").
        builder.Property(c => c.A2pAttestedAtUtc);
        builder.Property(c => c.A2pAttestedBy);
        builder.Property(c => c.A2pReference).HasMaxLength(NotificationChannelConfig.MaxA2pReferenceLength);
        // Inbound webhook token lifecycle (Wave 4 W2). All additive + nullable, so existing rows keep working: an org
        // that already has a token simply has no prefix/mint timestamp and no readiness proof — which correctly reads
        // as "not ready" to the send-path hold.
        builder.Property(c => c.InboundWebhookTokenPrefix)
            .HasMaxLength(NotificationChannelConfig.InboundWebhookTokenPrefixLength);
        builder.Property(c => c.InboundWebhookTokenMintedAtUtc);
        builder.Property(c => c.PreviousInboundWebhookTokenHash).HasMaxLength(128);
        builder.Property(c => c.PreviousInboundWebhookTokenExpiresAtUtc);
        builder.Property(c => c.InboundCurrentTokenVerifiedAtUtc);
        builder.Property(c => c.InboundPreviousTokenSeenAtUtc);
        builder.Property(c => c.InboundLastRejectedAtUtc);
        builder.Property(c => c.InboundRejectedCount).IsRequired().HasDefaultValue(0);
        builder.Property(c => c.Enabled).IsRequired();

        builder.HasIndex(c => new { c.OrganizationId, c.ChannelKind })
            .IsUnique()
            .HasDatabaseName("ux_notification_channel_configs_org_channel");

        // The inbound webhook resolves an org by token hash on an ANONYMOUS, cross-tenant IgnoreQueryFilters scan and
        // had no index at all. Filtered so they cost nothing for the (many) orgs with no SMS token.
        builder.HasIndex(c => c.InboundWebhookTokenHash)
            .HasDatabaseName("ix_notification_channel_configs_inbound_token")
            .HasFilter("inbound_webhook_token_hash IS NOT NULL");
        builder.HasIndex(c => c.PreviousInboundWebhookTokenHash)
            .HasDatabaseName("ix_notification_channel_configs_prev_inbound_token")
            .HasFilter("previous_inbound_webhook_token_hash IS NOT NULL");
    }
}

public sealed class SmsOptOutConfiguration : IEntityTypeConfiguration<SmsOptOut>
{
    public void Configure(EntityTypeBuilder<SmsOptOut> builder)
    {
        // The append-only SMS suppression ledger (docs/36 §4.3/§8, Wave 4 Slice D). Tenant-owned (organization_id) in
        // the reused `identity` schema, so rls.sql's data-driven tenant_isolation + wirehq_app grants cover it
        // automatically — no rls.sql change. Never mutated except the START clear (ClearedAtUtc); rows are never deleted.
        builder.ToTable("sms_opt_outs", "identity");
        builder.HasKey(o => o.Id);
        builder.Property(o => o.Id).ValueGeneratedNever();

        builder.Property(o => o.OrganizationId).IsRequired();
        builder.Property(o => o.PhoneE164).HasMaxLength(PhoneNumber.MaxLength).IsRequired();
        builder.Property(o => o.Sender).HasMaxLength(NotificationChannelConfig.MaxFromLength);
        builder.Property(o => o.Source).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(o => o.Keyword).HasMaxLength(32);
        // Kept separate from Keyword so a cleared row still records why the subscriber opted OUT (W2).
        builder.Property(o => o.ClearedKeyword).HasMaxLength(32);
        builder.Property(o => o.Reason).HasMaxLength(256);
        builder.Property(o => o.SuppressedAtUtc).IsRequired();
        builder.Property(o => o.ClearedAtUtc);

        // The (org, number) index backs both the resolve-time pre-filter and the send-time authoritative check that ask
        // "is this (org, number) currently suppressed?". The organization_id prefix also satisfies the RLS loop's key.
        builder.HasIndex(o => new { o.OrganizationId, o.PhoneE164 }).HasDatabaseName("ix_sms_opt_outs_organization_id_phone");
    }
}

public sealed class SmsConsentConfiguration : IEntityTypeConfiguration<SmsConsent>
{
    public void Configure(EntityTypeBuilder<SmsConsent> builder)
    {
        // Per-(org, user) SMS consent (docs/36 §4.1, Wave 4 Slice D precondition D-3). Tenant-owned (organization_id) in
        // the reused `identity` schema, so rls.sql's data-driven tenant_isolation + wirehq_app grants cover it
        // automatically — no rls.sql change. One row per (org, user).
        builder.ToTable("sms_consents", "identity");
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).ValueGeneratedNever();

        builder.Property(c => c.OrganizationId).IsRequired();
        builder.Property(c => c.UserId).IsRequired();
        builder.Property(c => c.Consented).IsRequired();
        builder.Property(c => c.ConsentedAtUtc);
        builder.Property(c => c.RevokedAtUtc);
        builder.Property(c => c.DisclosureVersion).HasMaxLength(32);
        builder.Property(c => c.UpdatedAtUtc).IsRequired();

        builder.HasIndex(c => new { c.OrganizationId, c.UserId })
            .IsUnique()
            .HasDatabaseName("ux_sms_consents_organization_id_user_id");
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
