using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WireHQ.Api.Controllers;
using WireHQ.Application.Features.Teams.CreateTeam;
using WireHQ.Application.Features.Teams.DeleteTeam;
using WireHQ.Application.Features.Teams.GetTeam;
using WireHQ.Application.Features.Teams.ListTeams;
using WireHQ.Application.Features.Teams.Members;
using WireHQ.Application.Features.Teams.UpdateTeam;

namespace WireHQ.Api.Controllers.V1;

/// <summary>
/// Teams — intra-tenant groupings within the active organization. Org-scoped, RBAC-gated
/// (<c>identity.teams.read|manage</c>); works for a customer admin and a Super Admin while
/// impersonating. (docs/03-multi-tenancy.md)
/// </summary>
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/teams")]
[Authorize]
public sealed class TeamsController : ApiControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string? search, CancellationToken cancellationToken) =>
        Ok(await Sender.Send(new ListTeamsQuery(search), cancellationToken));

    [HttpPost]
    public async Task<IActionResult> Create(CreateTeamRequest request, CancellationToken cancellationToken) =>
        Created(await Sender.Send(new CreateTeamCommand(request.Name, request.Description), cancellationToken));

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken) =>
        Ok(await Sender.Send(new GetTeamQuery(id), cancellationToken));

    [HttpPatch("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, UpdateTeamRequest request, CancellationToken cancellationToken) =>
        NoContent(await Sender.Send(new UpdateTeamCommand(id, request.Name, request.Description), cancellationToken));

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken) =>
        NoContent(await Sender.Send(new DeleteTeamCommand(id), cancellationToken));

    [HttpPost("{id:guid}/members")]
    public async Task<IActionResult> AddMember(Guid id, AddTeamMemberRequest request, CancellationToken cancellationToken) =>
        Created(await Sender.Send(new AddTeamMemberCommand(id, request.Email, request.Name, request.RoleId), cancellationToken));

    [HttpDelete("{id:guid}/members/{membershipId:guid}")]
    public async Task<IActionResult> RemoveMember(Guid id, Guid membershipId, CancellationToken cancellationToken) =>
        NoContent(await Sender.Send(new RemoveTeamMemberCommand(id, membershipId), cancellationToken));
}

public sealed record CreateTeamRequest(string Name, string? Description);

public sealed record UpdateTeamRequest(string? Name, string? Description);

public sealed record AddTeamMemberRequest(string Email, string? Name, Guid? RoleId);
