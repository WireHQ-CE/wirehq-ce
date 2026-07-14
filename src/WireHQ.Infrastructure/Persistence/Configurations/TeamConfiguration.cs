using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WireHQ.Domain.Teams;
using WireHQ.Domain.ValueObjects;

namespace WireHQ.Infrastructure.Persistence.Configurations;

public sealed class TeamConfiguration : IEntityTypeConfiguration<Team>
{
    public void Configure(EntityTypeBuilder<Team> builder)
    {
        builder.ToTable("teams", "core");
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).ValueGeneratedNever();
        builder.Ignore(t => t.DomainEvents);

        builder.Property(t => t.OrganizationId).IsRequired();
        builder.Property(t => t.Name).HasMaxLength(Team.MaxNameLength).IsRequired();
        builder.Property(t => t.Description).HasMaxLength(512);

        builder.OwnsOne(t => t.Slug, slug =>
            slug.Property(s => s.Value).HasColumnName("slug").HasMaxLength(Slug.MaxLength).IsRequired());
        builder.Navigation(t => t.Slug).IsRequired();

        // The org memberships in this team (an association, not a value owned by the team — so it
        // reliably appends to an already-persisted team). Configured in TeamMemberConfiguration.
        builder.HasMany(t => t.Members)
            .WithOne()
            .HasForeignKey(m => m.TeamId)
            .OnDelete(DeleteBehavior.Cascade);

        // Tenant-leading composite for fast per-org lookups. (Slug uniqueness-per-org is enforced
        // in the create handler, the same way OrganizationProvisioner guards the org slug.)
        builder.HasIndex(t => new { t.OrganizationId, t.Id });
        // Optimistic concurrency via Postgres' system xmin column (no stored column; bumped on every
        // UPDATE). A concurrent write fails with DbUpdateConcurrencyException (G-05 / HANDOFF gap #8).
        builder.Property<uint>("xmin").HasColumnName("xmin").HasColumnType("xid").IsRowVersion();
    }
}
