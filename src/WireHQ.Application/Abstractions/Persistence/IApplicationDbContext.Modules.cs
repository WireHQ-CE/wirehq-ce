using Microsoft.EntityFrameworkCore;
using WireHQ.Domain.Modules;

namespace WireHQ.Application.Abstractions.Persistence;

/// <summary>
/// The CE-only Marketplace module-activation slice of the persistence port
/// (docs/29-ce-marketplace-modules.md M-5). A partial interface ADDED by the Community Edition overlay — the
/// inverse of the SaaS-only marketplace/status slices, which are stripped: this exists only in the generated CE,
/// so the SaaS build's <c>IApplicationDbContext</c> never sees the <c>modules</c> schema. The tables are
/// platform-global (install-scoped, no tenant scoping), so a self-hosted install carries them install-wide.
/// </summary>
public partial interface IApplicationDbContext
{
    DbSet<ModuleLicence> ModuleLicences { get; }
    DbSet<InstallIdentity> InstallIdentities { get; }
}
