using Microsoft.EntityFrameworkCore;
using WireHQ.Application.Abstractions.Persistence;

namespace WireHQ.Modules.WireGuard.Persistence;

/// <summary>
/// Applies the module's entity configurations (schema <c>wg</c>) into the shared
/// <c>ApplicationDbContext</c> model. Registered in DI by the module so the context picks it up in
/// OnModelCreating — the module's tables join the model while its tenancy/audit/soft-delete filters
/// are applied generically by the context. (docs/11-wireguard-module.md §1.2/§1.3)
/// </summary>
public sealed class WireGuardModelContributor : IModelConfigurationContributor
{
    public void Configure(ModelBuilder modelBuilder) =>
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(WireGuardModelContributor).Assembly);
}
