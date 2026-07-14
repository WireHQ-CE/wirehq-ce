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

namespace WireHQ.Application.Features.Roles.CreateRole;

/// <summary>
/// Creates a custom role in the active organization (docs/25-custom-roles.md §4). Enterprise-gated
/// (<c>rbac.custom_roles</c>) + <c>identity.roles.manage</c>. The requested permissions are validated by the
/// privilege-escalation guard (an actor can only grant permissions it holds). Audited.
/// </summary>
public sealed record CreateRoleCommand(string Name, string? Description, IReadOnlyList<Guid> PermissionIds)
    : ICommand<CreateRoleResponse>, IAuthorizedRequest, IRequiresVerifiedEmail, IRequiresFeature
{
    public IReadOnlyCollection<string> RequiredPermissions => [Permissions.Roles.Manage];

    public string RequiredFeature => PlanFeatures.CustomRoles;
}

public sealed record CreateRoleResponse(Guid Id);

public sealed class CreateRoleCommandValidator : AbstractValidator<CreateRoleCommand>
{
    public CreateRoleCommandValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(Role.MaxNameLength);
        RuleFor(x => x.Description).MaximumLength(512);
    }
}

public sealed class CreateRoleCommandHandler(
    IApplicationDbContext dbContext, ITenantContext tenant, ICurrentUser currentUser, IAuditWriter audit)
    : ICommandHandler<CreateRoleCommand, CreateRoleResponse>
{
    public async Task<Result<CreateRoleResponse>> Handle(CreateRoleCommand command, CancellationToken cancellationToken)
    {
        if (tenant.OrganizationId is not { } organizationId)
        {
            return Error.Forbidden("auth.tenant_required", "No active organization.");
        }

        var name = command.Name.Trim();
        var normalized = name.ToLower();
        if (await dbContext.Roles.AnyAsync(r => r.Name.ToLower() == normalized, cancellationToken))
        {
            return RoleErrors.NameTaken; // avoid colliding with a system role or another custom role
        }

        var grantable = await GrantablePermissions.ValidateAsync(dbContext, currentUser, command.PermissionIds, [], cancellationToken);
        if (grantable.IsFailure)
        {
            return grantable.Error;
        }

        var roleResult = Role.Create(organizationId, name);
        if (roleResult.IsFailure)
        {
            return roleResult.Error;
        }

        var role = roleResult.Value;
        role.Describe(command.Description);
        role.SetPermissions(grantable.Value);
        dbContext.Roles.Add(role);

        audit.Record("identity.roles.created", AuditOutcome.Success, nameof(Role), role.Id.ToString(),
            new { role.Name, PermissionCount = grantable.Value.Count });

        return new CreateRoleResponse(role.Id);
    }
}
