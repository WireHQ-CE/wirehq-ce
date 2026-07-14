using Microsoft.EntityFrameworkCore;
using WireHQ.Application.Abstractions.Persistence;
using WireHQ.Application.Authorization;
using WireHQ.Application.Common.Messaging;
using WireHQ.Shared.Results;

namespace WireHQ.Application.Features.Roles.ListPermissions;

/// <summary>
/// The global permission catalog (id + key + group + description), grouped for the role editor's permission
/// picker. Not tenant data — the same catalogue for every org. <c>identity.roles.read</c>. (docs/25-custom-roles.md)
/// </summary>
public sealed record ListPermissionsQuery : IQuery<IReadOnlyList<PermissionListItem>>, IAuthorizedRequest
{
    public IReadOnlyCollection<string> RequiredPermissions => [Permissions.Roles.Read];
}

public sealed record PermissionListItem(Guid Id, string Key, string Group, string Description);

public sealed class ListPermissionsQueryHandler(IApplicationDbContext dbContext)
    : IQueryHandler<ListPermissionsQuery, IReadOnlyList<PermissionListItem>>
{
    public async Task<Result<IReadOnlyList<PermissionListItem>>> Handle(ListPermissionsQuery query, CancellationToken cancellationToken)
    {
        var permissions = await dbContext.Permissions
            .AsNoTracking()
            .OrderBy(p => p.Group)
            .ThenBy(p => p.Key)
            .Select(p => new PermissionListItem(p.Id, p.Key, p.Group, p.Description))
            .ToListAsync(cancellationToken);

        return Result.Success<IReadOnlyList<PermissionListItem>>(permissions);
    }
}
