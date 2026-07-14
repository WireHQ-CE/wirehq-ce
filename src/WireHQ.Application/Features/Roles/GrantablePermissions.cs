using Microsoft.EntityFrameworkCore;
using WireHQ.Application.Abstractions;
using WireHQ.Application.Abstractions.Persistence;
using WireHQ.Domain.Authorization;
using WireHQ.Shared.Results;

namespace WireHQ.Application.Features.Roles;

/// <summary>
/// The privilege-escalation guard for custom-role authoring (docs/25-custom-roles.md §6, ADR-042): an actor may
/// only grant a role permissions the actor's own effective set already holds — so an Admin can never mint a role
/// carrying Owner-only powers (e.g. <c>org.delete</c>). Validates that every requested permission id exists and is
/// held by the caller.
/// </summary>
internal static class GrantablePermissions
{
    /// <summary>
    /// Validates a requested permission set for a role. Every requested id must exist in the catalogue, and every
    /// <b>newly-added</b> permission (requested but not in <paramref name="currentIds"/>) must be one the actor
    /// holds — so escalation is blocked while removing or preserving an existing higher grant is always allowed
    /// (<paramref name="currentIds"/> is empty on create). Returns the validated (de-duplicated) requested ids.
    /// </summary>
    public static async Task<Result<IReadOnlyList<Guid>>> ValidateAsync(
        IApplicationDbContext dbContext, ICurrentUser actor,
        IReadOnlyList<Guid> requestedIds, IReadOnlyCollection<Guid> currentIds, CancellationToken cancellationToken)
    {
        var ids = requestedIds.Distinct().ToList();
        if (ids.Count == 0)
        {
            return Result.Success<IReadOnlyList<Guid>>(ids);
        }

        var permissions = await dbContext.Permissions
            .Where(p => ids.Contains(p.Id))
            .Select(p => new { p.Id, p.Key })
            .ToListAsync(cancellationToken);

        if (permissions.Count != ids.Count)
        {
            return RoleErrors.UnknownPermission; // an id that maps to no catalogue permission
        }

        var current = currentIds.ToHashSet();
        if (permissions.Any(p => !current.Contains(p.Id) && !actor.HasPermission(p.Key)))
        {
            return RoleErrors.PermissionNotGrantable; // you can't grant a permission you don't hold yourself
        }

        return Result.Success<IReadOnlyList<Guid>>(ids);
    }
}
