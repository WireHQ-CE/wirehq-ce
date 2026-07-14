using Microsoft.EntityFrameworkCore;

namespace WireHQ.Application.Abstractions.Persistence;

/// <summary>
/// A module's hook into the shared EF model. Each feature module registers one of these to apply
/// its <see cref="IEntityTypeConfiguration{TEntity}"/>s into its own Postgres schema, so module
/// entities live in the module's assembly + schema while reusing the single
/// <see cref="IApplicationDbContext"/> (and therefore the platform's tenancy, audit, domain-event,
/// and unit-of-work machinery). The DbContext resolves and invokes all contributors in
/// <c>OnModelCreating</c>. (docs/11-wireguard-module.md §1.3)
/// </summary>
public interface IModelConfigurationContributor
{
    void Configure(ModelBuilder modelBuilder);
}
