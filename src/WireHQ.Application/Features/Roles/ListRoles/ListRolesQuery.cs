using Microsoft.EntityFrameworkCore;
using WireHQ.Application.Abstractions.Persistence;
using WireHQ.Application.Authorization;
using WireHQ.Application.Common.Messaging;
using WireHQ.Shared.Results;

namespace WireHQ.Application.Features.Roles.ListRoles;

/// <summary>
/// Lists the active organization's roles (id + name + description + system flag), for role pickers such
/// as the Teams/Users invite dialog. Tenant-scoped via the <c>Role</c> query filter. (docs/03-multi-tenancy.md)
/// </summary>
public sealed record ListRolesQuery : IQuery<IReadOnlyList<RoleListItem>>, IAuthorizedRequest
{
    public IReadOnlyCollection<string> RequiredPermissions => [Permissions.Roles.Read];
}

public sealed record RoleListItem(Guid Id, string Name, string? Description, bool IsSystem);

public sealed class ListRolesQueryHandler(IApplicationDbContext dbContext)
    : IQueryHandler<ListRolesQuery, IReadOnlyList<RoleListItem>>
{
    public async Task<Result<IReadOnlyList<RoleListItem>>> Handle(ListRolesQuery query, CancellationToken cancellationToken)
    {
        var roles = await dbContext.Roles
            .OrderBy(r => r.Name)
            .Select(r => new RoleListItem(r.Id, r.Name, r.Description, r.IsSystem))
            .ToListAsync(cancellationToken);

        return Result.Success<IReadOnlyList<RoleListItem>>(roles);
    }
}
