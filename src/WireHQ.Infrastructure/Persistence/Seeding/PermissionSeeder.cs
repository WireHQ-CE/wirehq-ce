using Microsoft.EntityFrameworkCore;
using WireHQ.Application.Authorization;
using WireHQ.Domain.Authorization;

namespace WireHQ.Infrastructure.Persistence.Seeding;

/// <summary>
/// Idempotently syncs the global permission catalog into the database: the core
/// <see cref="Permissions.All"/> unioned with every module's <see cref="IPermissionContributor"/>.
/// Run on every startup so new permission keys (including module ones) land automatically; existing
/// keys are never duplicated.
/// </summary>
public sealed class PermissionSeeder(
    ApplicationDbContext dbContext,
    IEnumerable<IPermissionContributor> permissionContributors) : IDataSeeder
{
    // First: everything downstream (roles, the reconciler) needs the permission catalog current.
    public int Order => 10;

    public async Task SeedAsync(CancellationToken cancellationToken = default)
    {
        var existingKeys = await dbContext.Permissions
            .Select(p => p.Key)
            .ToListAsync(cancellationToken);

        var catalog = Permissions.All
            .Concat(permissionContributors.SelectMany(c => c.Permissions))
            .DistinctBy(def => def.Key);

        var missing = catalog
            .Where(def => !existingKeys.Contains(def.Key))
            .Select(def => new Permission(Guid.CreateVersion7(), def.Key, def.Group, def.Description))
            .ToList();

        if (missing.Count == 0)
        {
            return;
        }

        dbContext.Permissions.AddRange(missing);
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
