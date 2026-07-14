using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WireHQ.Domain.Authorization;

namespace WireHQ.Infrastructure.Persistence.Configurations;

public sealed class PermissionConfiguration : IEntityTypeConfiguration<Permission>
{
    public void Configure(EntityTypeBuilder<Permission> builder)
    {
        builder.ToTable("permissions", "identity");
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedNever();

        builder.Property(p => p.Key).HasMaxLength(128).IsRequired();
        builder.Property(p => p.Group).HasMaxLength(64).IsRequired();
        builder.Property(p => p.Description).HasMaxLength(256).IsRequired();

        // Global catalog — key is the stable, unique identifier code branches on.
        builder.HasIndex(p => p.Key).IsUnique();
    }
}

public sealed class RoleConfiguration : IEntityTypeConfiguration<Role>
{
    public void Configure(EntityTypeBuilder<Role> builder)
    {
        builder.ToTable("roles", "identity");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).ValueGeneratedNever();
        builder.Ignore(r => r.DomainEvents);

        builder.Property(r => r.OrganizationId).IsRequired();
        builder.Property(r => r.Name).HasMaxLength(Role.MaxNameLength).IsRequired();
        builder.Property(r => r.Description).HasMaxLength(256);
        builder.Property(r => r.IsSystem);

        builder.HasIndex(r => new { r.OrganizationId, r.Name }).IsUnique();

        // A role's granted permissions — a NORMAL child entity (not an owned collection), so appending a grant to
        // an already-persisted role (the role editor's update path) emits an INSERT instead of tripping the
        // owned-collection append gotcha (a composite-keyed owned append is silently treated as an UPDATE). Same
        // table + keys as before — see RolePermissionConfiguration. (docs/25-custom-roles.md; the SsoRoleMapping lesson)
        builder.HasMany(r => r.Permissions)
            .WithOne()
            .HasForeignKey(p => p.RoleId)
            .OnDelete(DeleteBehavior.Cascade);

        // Optimistic concurrency via Postgres' system xmin column (no stored column; bumped on every
        // UPDATE). A concurrent write fails with DbUpdateConcurrencyException (G-05 / HANDOFF gap #8).
        builder.Property<uint>("xmin").HasColumnName("xmin").HasColumnType("xid").IsRowVersion();
    }
}

public sealed class RolePermissionConfiguration : IEntityTypeConfiguration<RolePermission>
{
    public void Configure(EntityTypeBuilder<RolePermission> builder)
    {
        builder.ToTable("role_permissions", "identity");
        builder.HasKey(p => new { p.RoleId, p.PermissionId });
        builder.Property(p => p.PermissionId).IsRequired();
        builder.HasIndex(p => p.PermissionId);
    }
}
