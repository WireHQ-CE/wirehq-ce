using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WireHQ.Modules.Orchestration.Domain;

namespace WireHQ.Modules.Orchestration.Persistence.Configurations;

public sealed class SshTargetConfiguration : IEntityTypeConfiguration<SshTarget>
{
    public void Configure(EntityTypeBuilder<SshTarget> builder)
    {
        builder.ToTable("ssh_targets", "orch");
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).ValueGeneratedNever();
        builder.Ignore(t => t.DomainEvents);

        builder.Property(t => t.OrganizationId).IsRequired();
        builder.Property(t => t.Name).HasMaxLength(SshTarget.MaxNameLength).IsRequired();
        builder.Property(t => t.Host).HasMaxLength(256).IsRequired();
        builder.Property(t => t.Username).HasMaxLength(64).IsRequired();
        builder.Property(t => t.AuthKind).HasConversion<string>().HasMaxLength(16);
        builder.Property(t => t.CredentialCiphertext).IsRequired();
        builder.Property(t => t.HostKeyFingerprint).HasMaxLength(128);

        builder.HasIndex(t => new { t.OrganizationId, t.Id });
    }
}
