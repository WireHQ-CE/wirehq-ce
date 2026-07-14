using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WireHQ.Domain.Memberships;

namespace WireHQ.Infrastructure.Persistence.Configurations;

public sealed class MembershipConfiguration : IEntityTypeConfiguration<Membership>
{
    public void Configure(EntityTypeBuilder<Membership> builder)
    {
        builder.ToTable("memberships", "core");
        builder.HasKey(m => m.Id);
        builder.Property(m => m.Id).ValueGeneratedNever();
        builder.Ignore(m => m.DomainEvents);

        builder.Property(m => m.OrganizationId).IsRequired();
        builder.Property(m => m.UserId).IsRequired();
        builder.Property(m => m.Status).HasConversion<string>().HasMaxLength(32);

        // One membership per (org, user) — enforced for live rows only.
        builder.HasIndex(m => new { m.OrganizationId, m.UserId })
            .IsUnique()
            .HasFilter("is_deleted = false");

        builder.HasIndex(m => m.UserId);

        // A user's roles within this org, owned by the membership aggregate.
        builder.OwnsMany(m => m.Roles, role =>
        {
            role.ToTable("membership_roles", "identity");
            role.WithOwner().HasForeignKey(r => r.MembershipId);
            role.HasKey(r => new { r.MembershipId, r.RoleId });
            role.Property(r => r.RoleId).IsRequired();
            role.HasIndex(r => r.RoleId);
        });

        // Optimistic concurrency via Postgres' system xmin column (no stored column; bumped on every
        // UPDATE). A concurrent write fails with DbUpdateConcurrencyException (G-05 / HANDOFF gap #8).
        builder.Property<uint>("xmin").HasColumnName("xmin").HasColumnType("xid").IsRowVersion();
    }
}
