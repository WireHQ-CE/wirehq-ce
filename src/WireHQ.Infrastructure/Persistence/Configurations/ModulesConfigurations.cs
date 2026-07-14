using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WireHQ.Domain.Modules;

namespace WireHQ.Infrastructure.Persistence.Configurations;

// The CE Marketplace module-activation entity configurations (docs/29-ce-marketplace-modules.md M-5). Grouped in
// one file, overlay-added CE-only — the SaaS build never carries them, so its EF model has no `modules` schema.
// Everything here lives in the platform-global `modules` schema: install-scoped, no `organization_id`, so the
// data-driven rls.sql tenant policy does not apply (the schema's grant is guarded on existence in rls.sql, so it
// no-ops on the SaaS build). Index names are set explicitly because EF derives them from table+column ignoring
// the schema — the explicit names keep them unambiguous across schemas.

public sealed class ModuleLicenceConfiguration : IEntityTypeConfiguration<ModuleLicence>
{
    public void Configure(EntityTypeBuilder<ModuleLicence> builder)
    {
        builder.ToTable("module_licences", "modules");
        builder.HasKey(l => l.Id);
        builder.Property(l => l.Id).ValueGeneratedNever();

        builder.Property(l => l.ModuleSlug).HasMaxLength(64).IsRequired();
        builder.Property(l => l.LicenceId).HasMaxLength(64).IsRequired();
        builder.Property(l => l.LicenceKey).HasMaxLength(2048).IsRequired();
        builder.Property(l => l.Status).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(l => l.ActivationToken).HasMaxLength(2048).IsRequired();

        // One activation per module on an install — re-entering a key for an already-active module is a conflict,
        // and deactivation removes the row so it can be re-activated (or moved) later.
        builder.HasIndex(l => l.ModuleSlug).IsUnique().HasDatabaseName("ix_modules_module_licences_module_slug");
    }
}

public sealed class InstallIdentityConfiguration : IEntityTypeConfiguration<InstallIdentity>
{
    public void Configure(EntityTypeBuilder<InstallIdentity> builder)
    {
        builder.ToTable("install_identity", "modules");
        // The key is the fixed singleton id (InstallIdentity.SingletonId) — a concurrent second insert collides
        // on the PK rather than minting a duplicate identity, keeping the table a true singleton (docs/29 M-5).
        builder.HasKey(i => i.Id);
        builder.Property(i => i.Id).ValueGeneratedNever();

        // The random per-install fingerprint (a stored column, unique per install; distinct from the constant key).
        builder.Property(i => i.Fingerprint).HasMaxLength(64).IsRequired();
    }
}
