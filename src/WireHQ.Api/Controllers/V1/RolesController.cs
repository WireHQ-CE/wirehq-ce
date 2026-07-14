using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WireHQ.Api.Controllers;
using WireHQ.Application.Features.Roles.CreateRole;
using WireHQ.Application.Features.Roles.DeleteRole;
using WireHQ.Application.Features.Roles.GetRole;
using WireHQ.Application.Features.Roles.ListPermissions;
using WireHQ.Application.Features.Roles.ListRoles;
using WireHQ.Application.Features.Roles.UpdateRole;

namespace WireHQ.Api.Controllers.V1;

/// <summary>
/// Roles in the active organization (docs/25-custom-roles.md). Read is available to any org member with
/// <c>identity.roles.read</c> (for role pickers); creating/editing/deleting <b>custom</b> roles needs
/// <c>identity.roles.manage</c> and the <c>rbac.custom_roles</c> entitlement (Enterprise — and CE self-hosters,
/// who default to Enterprise). System roles are immutable. Org-scoped; kept-core (no CE strip — this operates on
/// the core <c>Role</c> aggregate, entitlement-gated rather than SaaS-only).
/// </summary>
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/roles")]
[Authorize]
public sealed class RolesController : ApiControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken cancellationToken) =>
        Ok(await Sender.Send(new ListRolesQuery(), cancellationToken));

    /// <summary>The global permission catalog (grouped) for the role editor's permission picker.</summary>
    [HttpGet("permissions")]
    public async Task<IActionResult> Permissions(CancellationToken cancellationToken) =>
        Ok(await Sender.Send(new ListPermissionsQuery(), cancellationToken));

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken) =>
        Ok(await Sender.Send(new GetRoleQuery(id), cancellationToken));

    [HttpPost]
    public async Task<IActionResult> Create(UpsertRoleRequest request, CancellationToken cancellationToken) =>
        Created(await Sender.Send(new CreateRoleCommand(request.Name, request.Description, request.PermissionIds ?? []), cancellationToken));

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, UpsertRoleRequest request, CancellationToken cancellationToken) =>
        NoContent(await Sender.Send(new UpdateRoleCommand(id, request.Name, request.Description, request.PermissionIds ?? []), cancellationToken));

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken) =>
        NoContent(await Sender.Send(new DeleteRoleCommand(id), cancellationToken));
}

/// <summary>Create/update body for a custom role — a name, an optional description, and the permission ids to grant.</summary>
public sealed record UpsertRoleRequest(string Name, string? Description, IReadOnlyList<Guid>? PermissionIds);
