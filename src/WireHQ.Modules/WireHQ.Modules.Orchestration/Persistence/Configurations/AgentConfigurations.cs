using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WireHQ.Modules.Orchestration.Domain;

namespace WireHQ.Modules.Orchestration.Persistence.Configurations;

public sealed class AgentConfiguration : IEntityTypeConfiguration<Agent>
{
    public void Configure(EntityTypeBuilder<Agent> builder)
    {
        builder.ToTable("agents", "orch");
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).ValueGeneratedNever();
        builder.Ignore(a => a.DomainEvents);

        builder.Property(a => a.OrganizationId).IsRequired();
        builder.Property(a => a.Name).HasMaxLength(Agent.MaxNameLength).IsRequired();
        builder.Property(a => a.CertificateFingerprint).HasMaxLength(95).IsRequired();
        builder.Property(a => a.CertificatePem).IsRequired();
        builder.Property(a => a.Status).HasConversion<string>().HasMaxLength(16);
        builder.Property(a => a.Platform).HasMaxLength(64);
        builder.Property(a => a.Version).HasMaxLength(32);

        // The gateway authenticates every agent request by the SHA-256 fingerprint of the presented client
        // cert — the hot lookup. Globally unique: each issued cert carries a random serial (distinct hash).
        builder.HasIndex(a => a.CertificateFingerprint).IsUnique();
        builder.HasIndex(a => new { a.OrganizationId, a.Id });
    }
}

public sealed class AgentEnrollmentTokenConfiguration : IEntityTypeConfiguration<AgentEnrollmentToken>
{
    public void Configure(EntityTypeBuilder<AgentEnrollmentToken> builder)
    {
        builder.ToTable("agent_enrollment_tokens", "orch");
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).ValueGeneratedNever();
        builder.Ignore(t => t.DomainEvents);

        builder.Property(t => t.OrganizationId).IsRequired();
        builder.Property(t => t.TokenHash).HasMaxLength(95).IsRequired();

        // The enrol endpoint looks the token up by hash before burning it.
        builder.HasIndex(t => t.TokenHash).IsUnique();
        builder.HasIndex(t => new { t.OrganizationId, t.Id });
    }
}

public sealed class OrgCertificateAuthorityConfiguration : IEntityTypeConfiguration<OrgCertificateAuthority>
{
    public void Configure(EntityTypeBuilder<OrgCertificateAuthority> builder)
    {
        builder.ToTable("org_certificate_authorities", "orch");
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).ValueGeneratedNever();
        builder.Ignore(c => c.DomainEvents);

        builder.Property(c => c.OrganizationId).IsRequired();
        builder.Property(c => c.CertificatePem).IsRequired();
        builder.Property(c => c.PrivateKeyCiphertext).IsRequired();

        // Exactly one CA per organization — also guards the lazy-create race fail-closed.
        builder.HasIndex(c => c.OrganizationId).IsUnique();
    }
}
