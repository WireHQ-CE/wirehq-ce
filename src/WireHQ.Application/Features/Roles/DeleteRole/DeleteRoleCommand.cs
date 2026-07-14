using Microsoft.EntityFrameworkCore;
using WireHQ.Application.Abstractions;
using WireHQ.Application.Abstractions.Persistence;
using WireHQ.Application.Authorization;
using WireHQ.Application.Common.Messaging;
using WireHQ.Application.Entitlements;
using WireHQ.Domain.Auditing;
using WireHQ.Domain.Authorization;
using WireHQ.Shared.Results;

namespace WireHQ.Application.Features.Roles.DeleteRole;

/// <summary>
/// Deletes a custom role (docs/25-custom-roles.md §4). Refused on a system role (<c>SystemRoleImmutable</c>) or a
/// role still assigned to any member (<c>role.in_use</c> — reassign first, R-5). Enterprise-gated
/// (<c>rbac.custom_roles</c>) + <c>identity.roles.manage</c>; audited. The owned <c>role_permissions</c> rows
/// cascade with the aggregate.
/// </summary>
public sealed record DeleteRoleCommand(Guid Id)
    : ICommand, IAuthorizedRequest, IRequiresFeature
{
    public IReadOnlyCollection<string> RequiredPermissions => [Permissions.Roles.Manage];

    public string RequiredFeature => PlanFeatures.CustomRoles;
}

public sealed class DeleteRoleCommandHandler(IApplicationDbContext dbContext, IAuditWriter audit)
    : ICommandHandler<DeleteRoleCommand>
{
    public async Task<Result> Handle(DeleteRoleCommand command, CancellationToken cancellationToken)
    {
        var role = await dbContext.Roles.FirstOrDefaultAsync(r => r.Id == command.Id, cancellationToken);
        if (role is null)
        {
            return RoleErrors.NotFound;
        }

        if (role.IsSystem)
        {
            return RoleErrors.SystemRoleImmutable;
        }

        var inUse = await dbContext.Memberships.AnyAsync(m => m.Roles.Any(r => r.RoleId == role.Id), cancellationToken);
        if (inUse)
        {
            return RoleErrors.InUse;
        }

        dbContext.Roles.Remove(role);

        audit.Record("identity.roles.deleted", AuditOutcome.Success, nameof(Role), role.Id.ToString(), new { role.Name });

        return Result.Success();
    }
}
