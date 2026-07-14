using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WireHQ.Api.Controllers;
using WireHQ.Application.Features.Users.InviteUser;
using WireHQ.Application.Features.Users.ListUsers;

namespace WireHQ.Api.Controllers.V1;

[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/users")]
[Authorize]
public sealed class UsersController : ApiControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string? search, [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken cancellationToken = default) =>
        Ok(await Sender.Send(new ListUsersQuery(search, page, pageSize), cancellationToken));

    [HttpPost("invitations")]
    public async Task<IActionResult> Invite(InviteUserRequest request, CancellationToken cancellationToken)
    {
        var result = await Sender.Send(
            new InviteUserCommand(request.Email, request.Name, request.RoleIds),
            cancellationToken);

        return Created(result);
    }
}

public sealed record InviteUserRequest(string Email, string? Name, IReadOnlyCollection<Guid>? RoleIds);
