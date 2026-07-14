using FluentValidation;
using Microsoft.EntityFrameworkCore;
using WireHQ.Application.Abstractions;
using WireHQ.Application.Abstractions.Persistence;
using WireHQ.Application.Authorization;
using WireHQ.Application.Common.Messaging;
using WireHQ.Application.Entitlements;
using WireHQ.Domain.Auditing;
using WireHQ.Domain.Authorization;
using WireHQ.Shared.Results;

namespace WireHQ.Application.Features.Roles.UpdateRole;

/// <summary>
/// Renames/describes a custom role and REPLACES its permission set (docs/25-custom-roles.md §4). Refused on a
/// system role (<c>SystemRoleImmutable</c>). Enterprise-gated (<c>rbac.custom_roles</c>) + <c>identity.roles.manage</c>;
/// the added permissions are checked by the privilege-escalation guard (removing/keeping existing grants is fine).
/// </summary>
public sealed record UpdateRoleCommand(Guid Id, string Name, string? Description, IReadOnlyList<Guid> PermissionIds)
    : ICommand, IAuthorizedRequest, IRequiresVerifiedEmail, IRequiresFeature
{
    public IReadOnlyCollection<string> RequiredPermissions => [Permissions.Roles.Manage];

    public string RequiredFeature => PlanFeatures.CustomRoles;
}

public sealed class UpdateRoleCommandValidator : AbstractValidator<UpdateRoleCommand>
{
    public UpdateRoleCommandValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(Role.MaxNameLength);
        RuleFor(x => x.Description).MaximumLength(512);
    }
}

public sealed class UpdateRoleCommandHandler(
    IApplicationDbContext dbContext, ICurrentUser currentUser, IAuditWriter audit)
    : ICommandHandler<UpdateRoleCommand>
{
    public async Task<Result> Handle(UpdateRoleCommand command, CancellationToken cancellationToken)
    {
        // Permissions is a normal (non-owned) navigation, so it must be explicitly included to diff the set.
        var role = await dbContext.Roles.Include(r => r.Permissions).FirstOrDefaultAsync(r => r.Id == command.Id, cancellationToken);
        if (role is null)
        {
            return RoleErrors.NotFound;
        }

        if (role.IsSystem)
        {
            return RoleErrors.SystemRoleImmutable;
        }

        var name = command.Name.Trim();
        var normalized = name.ToLower();
        if (await dbContext.Roles.AnyAsync(r => r.Id != role.Id && r.Name.ToLower() == normalized, cancellationToken))
        {
            return RoleErrors.NameTaken;
        }

        var current = role.Permissions.Select(p => p.PermissionId).ToList();
        var grantable = await GrantablePermissions.ValidateAsync(dbContext, currentUser, command.PermissionIds, current, cancellationToken);
        if (grantable.IsFailure)
        {
            return grantable.Error;
        }

        var renamed = role.Rename(name);
        if (renamed.IsFailure)
        {
            return renamed.Error;
        }

        role.Describe(command.Description);
        role.SetPermissions(grantable.Value);

        audit.Record("identity.roles.updated", AuditOutcome.Success, nameof(Role), role.Id.ToString(),
            new { role.Name, PermissionCount = grantable.Value.Count });

        return Result.Success();
    }
}
