using Microsoft.EntityFrameworkCore;
using WireHQ.Domain.Modules;

namespace WireHQ.Infrastructure.Persistence;

/// <summary>
/// The CE-only Marketplace module-activation slice of the concrete context
/// (docs/29-ce-marketplace-modules.md M-5). A partial ADDED by the Community Edition overlay (the inverse of the
/// stripped marketplace/status partials): the entity types + their configurations join the EF model only in the
/// generated CE, so the CE's regenerated <c>InitialCreate</c> creates the <c>modules</c> schema while the SaaS
/// build carries neither the schema nor these DbSets. Configurations are assembly-scanned, so nothing else
/// changes.
/// </summary>
public sealed partial class ApplicationDbContext
{
    public DbSet<ModuleLicence> ModuleLicences => Set<ModuleLicence>();
    public DbSet<InstallIdentity> InstallIdentities => Set<InstallIdentity>();
}
