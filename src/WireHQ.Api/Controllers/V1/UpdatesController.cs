using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WireHQ.Api.Controllers;
using WireHQ.Api.Observability;
using WireHQ.Application.Updates;

namespace WireHQ.Api.Controllers.V1;

/// <summary>
/// Reports whether a newer WireHQ version is available, for the in-app update banner/modal (docs/30). Kept-core:
/// SaaS (WireHQ-operated, auto-deployed) binds the no-op provider and always reports up-to-date; a Community
/// Edition binds the signed-manifest poller. Operator-gated at the use-case level (org.settings.update).
/// </summary>
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/updates")]
[Authorize]
public sealed class UpdatesController : ApiControllerBase
{
    /// <summary>The install's update situation. The running version is the server's build-stamped version.</summary>
    [HttpGet("status")]
    public async Task<IActionResult> Status(CancellationToken cancellationToken) =>
        Ok(await Sender.Send(new GetUpdateStatusQuery(ObservabilityResource.Version), cancellationToken));
}
