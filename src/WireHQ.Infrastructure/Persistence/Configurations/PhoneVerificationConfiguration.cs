using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WireHQ.Domain.Identity;
using WireHQ.Domain.ValueObjects;

namespace WireHQ.Infrastructure.Persistence.Configurations;

/// <summary>
/// The phone-ownership challenge store (docs/36-notifications.md §8, SMS Wave 4). Tenant-owned (organization_id) in the
/// reused <c>identity</c> schema, so rls.sql's data-driven tenant_isolation + wirehq_app grants cover it automatically —
/// no rls.sql change. The OTP is stored ONLY as a salted hash (code_hash); the plaintext code is never persisted. The
/// (created_at_utc) + (phone_e164) indexes back the per-user / per-org / per-number rate-limit window scans.
/// </summary>
public sealed class PhoneVerificationConfiguration : IEntityTypeConfiguration<PhoneVerification>
{
    public void Configure(EntityTypeBuilder<PhoneVerification> builder)
    {
        builder.ToTable("phone_verifications", "identity");
        builder.HasKey(v => v.Id);
        builder.Property(v => v.Id).ValueGeneratedNever();

        builder.Property(v => v.OrganizationId).IsRequired();
        builder.Property(v => v.UserId).IsRequired();
        builder.Property(v => v.PhoneE164).HasMaxLength(PhoneNumber.MaxLength).IsRequired();
        builder.Property(v => v.CodeHash).HasMaxLength(128).IsRequired();
        builder.Property(v => v.Attempts).IsRequired();
        builder.Property(v => v.CreatedAtUtc).IsRequired();
        builder.Property(v => v.ExpiresAtUtc).IsRequired();
        builder.Property(v => v.ConsumedAtUtc);

        builder.HasIndex(v => new { v.UserId, v.CreatedAtUtc }).HasDatabaseName("ix_phone_verifications_user_created");
        builder.HasIndex(v => new { v.PhoneE164, v.CreatedAtUtc }).HasDatabaseName("ix_phone_verifications_phone_created");
        builder.HasIndex(v => v.OrganizationId).HasDatabaseName("ix_phone_verifications_organization_id");
    }
}
