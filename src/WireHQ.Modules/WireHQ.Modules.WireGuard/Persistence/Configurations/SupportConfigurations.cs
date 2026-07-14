using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WireHQ.Modules.WireGuard.Domain;

namespace WireHQ.Modules.WireGuard.Persistence.Configurations;

public sealed class KeyMaterialConfiguration : IEntityTypeConfiguration<KeyMaterial>
{
    public void Configure(EntityTypeBuilder<KeyMaterial> builder)
    {
        builder.ToTable("key_material", "wg");
        builder.HasKey(k => k.Id);
        builder.Property(k => k.Id).ValueGeneratedNever();

        builder.Property(k => k.OrganizationId).IsRequired();
        builder.Property(k => k.OwnerType).HasConversion<string>().HasMaxLength(16);
        builder.Property(k => k.Kind).HasConversion<string>().HasMaxLength(16);
        builder.Property(k => k.Ciphertext).HasMaxLength(2048).IsRequired();
        builder.Property(k => k.PublicKey).HasMaxLength(64);
        builder.Property(k => k.Status).HasConversion<string>().HasMaxLength(16);

        builder.HasIndex(k => new { k.OwnerType, k.OwnerId });
    }
}

public sealed class ConfigVersionConfiguration : IEntityTypeConfiguration<ConfigVersion>
{
    public void Configure(EntityTypeBuilder<ConfigVersion> builder)
    {
        builder.ToTable("config_versions", "wg");
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).ValueGeneratedNever();

        builder.Property(c => c.OrganizationId).IsRequired();
        builder.Property(c => c.TargetType).HasConversion<string>().HasMaxLength(16);
        builder.Property(c => c.Format).HasMaxLength(32);
        builder.Property(c => c.ContentEncrypted).IsRequired();
        builder.Property(c => c.Checksum).HasMaxLength(64).IsRequired();
        builder.Property(c => c.Note).HasMaxLength(512);

        builder.HasIndex(c => new { c.TargetType, c.TargetId, c.Version }).IsUnique();
    }
}

public sealed class EnrollmentBatchConfiguration : IEntityTypeConfiguration<EnrollmentBatch>
{
    public void Configure(EntityTypeBuilder<EnrollmentBatch> builder)
    {
        builder.ToTable("enrollment_batches", "wg");
        builder.HasKey(b => b.Id);
        builder.Property(b => b.Id).ValueGeneratedNever();

        builder.Property(b => b.OrganizationId).IsRequired();
        builder.Property(b => b.InstanceId).IsRequired();
        builder.Property(b => b.SourceFilename).HasMaxLength(256).IsRequired();
        builder.Property(b => b.Status).HasConversion<string>().HasMaxLength(16);
        builder.Property(b => b.Summary).HasColumnType("jsonb");

        builder.HasIndex(b => new { b.OrganizationId, b.InstanceId });
    }
}
