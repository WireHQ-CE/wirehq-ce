using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WireHQ.Domain.Auditing;

namespace WireHQ.Infrastructure.Persistence.Configurations;

public sealed class AuditChainAnchorConfiguration : IEntityTypeConfiguration<AuditChainAnchor>
{
    public void Configure(EntityTypeBuilder<AuditChainAnchor> builder)
    {
        builder.ToTable("audit_chain_anchors", "audit");
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).ValueGeneratedNever();

        builder.Property(a => a.BoundaryPrevHash).HasColumnType("bytea").IsRequired();

        // The verifier looks anchors up by (org, boundary hash) when checking a chain's first surviving row.
        builder.HasIndex(a => new { a.OrganizationId, a.BoundaryPrevHash });
    }
}
