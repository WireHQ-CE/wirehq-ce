using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WireHQ.Api.Controllers;
using WireHQ.Application.Features.Account.Avatar;

namespace WireHQ.Api.Controllers.V1;

/// <summary>
/// Public avatar images. Anonymous so they render in plain <c>&lt;img&gt;</c> tags (no bearer token);
/// avatars are low-sensitivity. Cache-busting is handled by the <c>?v=</c> query the client appends.
/// </summary>
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/avatars")]
[AllowAnonymous]
public sealed class AvatarsController : ApiControllerBase
{
    [HttpGet("{userId:guid}")]
    public async Task<IActionResult> Get(Guid userId, CancellationToken cancellationToken)
    {
        var result = await Sender.Send(new GetAvatarQuery(userId), cancellationToken);
        if (result.IsFailure)
        {
            return Problem(result.Error);
        }

        Response.Headers.CacheControl = "public, max-age=300";
        return File(result.Value.Data, result.Value.ContentType);
    }
}
