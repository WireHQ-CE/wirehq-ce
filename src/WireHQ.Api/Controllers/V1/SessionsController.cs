using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WireHQ.Api.Controllers;
using WireHQ.Application.Features.Sessions.ListSessions;
using WireHQ.Application.Features.Sessions.RevokeAllSessions;
using WireHQ.Application.Features.Sessions.RevokeSession;

namespace WireHQ.Api.Controllers.V1;

/// <summary>The signed-in user's active sessions across devices.</summary>
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/sessions")]
[Authorize]
public sealed class SessionsController : ApiControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken cancellationToken) =>
        Ok(await Sender.Send(new ListSessionsQuery(), cancellationToken));

    [HttpDelete("{sessionId:guid}")]
    public async Task<IActionResult> Revoke(Guid sessionId, CancellationToken cancellationToken) =>
        NoContent(await Sender.Send(new RevokeSessionCommand(sessionId), cancellationToken));

    /// <summary>Log out everywhere — revokes all sessions except the current one.</summary>
    [HttpPost("revoke-all")]
    public async Task<IActionResult> RevokeAll(CancellationToken cancellationToken) =>
        NoContent(await Sender.Send(new RevokeAllSessionsCommand(), cancellationToken));
}
