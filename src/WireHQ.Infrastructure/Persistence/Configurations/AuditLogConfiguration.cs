using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WireHQ.Domain.Auditing;

namespace WireHQ.Infrastructure.Persistence.Configurations;

public sealed class AuditLogConfiguration : IEntityTypeConfiguration<AuditLog>
{
    public void Configure(EntityTypeBuilder<AuditLog> builder)
    {
        builder.ToTable("audit_logs", "audit");
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).ValueGeneratedNever();

        builder.Property(a => a.ActorType).HasMaxLength(32).IsRequired();
        builder.Property(a => a.ActorEmail).HasMaxLength(256);
        builder.Property(a => a.Action).HasMaxLength(128).IsRequired();
        builder.Property(a => a.TargetType).HasMaxLength(64);
        builder.Property(a => a.TargetId).HasMaxLength(64);
        builder.Property(a => a.Outcome).HasConversion<string>().HasMaxLength(16);
        // `json`, not `jsonb`: the diff is stored verbatim (no whitespace/key-order normalisation) so the
        // tamper-evidence hash, computed over the exact text, reproduces byte-for-byte at verification. (ADR-031)
        builder.Property(a => a.Changes).HasColumnType("json");
        builder.Property(a => a.IpAddress).HasMaxLength(64);
        builder.Property(a => a.UserAgent).HasMaxLength(512);
        builder.Property(a => a.RequestId).HasMaxLength(64);

        // Tamper-evidence: the per-tenant hash chain (ADR-031). SHA-256 ⇒ 32 bytes; null only for the
        // genesis entry (prev_hash) and rows written before the hash-chain migration.
        builder.Property(a => a.PrevHash).HasColumnType("bytea");
        builder.Property(a => a.EntryHash).HasColumnType("bytea");

        // The tenant audit feed, plus entity-history and actor lookups.
        builder.HasIndex(a => new { a.OrganizationId, a.OccurredAtUtc });
        builder.HasIndex(a => new { a.TargetType, a.TargetId });
        builder.HasIndex(a => a.ActorUserId);
    }
}
