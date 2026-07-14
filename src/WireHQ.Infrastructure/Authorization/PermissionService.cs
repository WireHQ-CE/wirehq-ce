using Microsoft.EntityFrameworkCore;
using WireHQ.Application.Abstractions;
using WireHQ.Application.Abstractions.Persistence;

namespace WireHQ.Infrastructure.Authorization;

/// <summary>
/// Resolves the effective permission set for a membership: its roles' permissions, unioned and
/// mapped to stable keys. Query filters are ignored because this runs both at login (before a
/// tenant is established) and per request. Results are small and cacheable per session.
/// </summary>
public sealed class PermissionService(IApplicationDbContext dbContext) : IPermissionService
{
    public async Task<IReadOnlyCollection<string>> GetEffectivePermissionsAsync(
        Guid membershipId, CancellationToken cancellationToken)
    {
        var roleIds = await dbContext.Memberships
            .IgnoreQueryFilters()
            .Where(m => m.Id == membershipId)
            .SelectMany(m => m.Roles.Select(r => r.RoleId))
            .ToListAsync(cancellationToken);

        if (roleIds.Count == 0)
        {
            return [];
        }

        var permissionIds = await dbContext.Roles
            .IgnoreQueryFilters()
            .Where(r => roleIds.Contains(r.Id))
            .SelectMany(r => r.Permissions.Select(p => p.PermissionId))
            .Distinct()
            .ToListAsync(cancellationToken);

        if (permissionIds.Count == 0)
        {
            return [];
        }

        return await dbContext.Permissions
            .Where(p => permissionIds.Contains(p.Id))
            .Select(p => p.Key)
            .ToListAsync(cancellationToken);
    }
}
