using Microsoft.EntityFrameworkCore;
using WireHQ.Application.Abstractions.Persistence;
using WireHQ.Application.Authorization;
using WireHQ.Application.Common.Messaging;
using WireHQ.Domain.Authorization;
using WireHQ.Shared.Results;

namespace WireHQ.Application.Features.Roles.GetRole;

/// <summary>
/// A single role's detail (name, description, system flag, and the ids of the permissions it grants) for the
/// role editor. Tenant-scoped via the <c>Role</c> query filter; <c>identity.roles.read</c>. (docs/25-custom-roles.md)
/// </summary>
public sealed record GetRoleQuery(Guid Id) : IQuery<RoleDetail>, IAuthorizedRequest
{
    public IReadOnlyCollection<string> RequiredPermissions => [Permissions.Roles.Read];
}

public sealed record RoleDetail(Guid Id, string Name, string? Description, bool IsSystem, IReadOnlyList<Guid> PermissionIds);

public sealed class GetRoleQueryHandler(IApplicationDbContext dbContext)
    : IQueryHandler<GetRoleQuery, RoleDetail>
{
    public async Task<Result<RoleDetail>> Handle(GetRoleQuery query, CancellationToken cancellationToken)
    {
        var role = await dbContext.Roles
            .AsNoTracking()
            .Where(r => r.Id == query.Id)
            .Select(r => new RoleDetail(
                r.Id, r.Name, r.Description, r.IsSystem,
                r.Permissions.Select(p => p.PermissionId).ToList()))
            .FirstOrDefaultAsync(cancellationToken);

        return role is null ? RoleErrors.NotFound : role;
    }
}
