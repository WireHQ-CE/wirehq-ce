using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WireHQ.Api.Controllers;
using WireHQ.Application.Features.Modules;

namespace WireHQ.Api.Controllers.V1;

/// <summary>
/// The Community Edition Marketplace module-activation console API (docs/29-ce-marketplace-modules.md M-9).
/// A self-hoster activates a purchased module by entering its licence key here; the capability then lights up
/// via the entitlement union. Gated on <c>marketplace.modules.manage</c>. CE-ONLY: this controller is
/// overlay-added, so it ships only in the generated Community Edition — the SaaS build (which unlocks capability
/// through plan bundles) never exposes it.
/// </summary>
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/modules")]
[Authorize]
public sealed class ModulesController : ApiControllerBase
{
    /// <summary>The modules currently activated on this install and their state.</summary>
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken cancellationToken) =>
        Ok(await Sender.Send(new ListModulesQuery(), cancellationToken));

    /// <summary>Activate a module by its licence key → returns the module the key unlocked.</summary>
    [HttpPost("activate")]
    public async Task<IActionResult> Activate(ActivateModuleRequest request, CancellationToken cancellationToken) =>
        Ok(await Sender.Send(new ActivateModuleCommand(request.LicenceKey), cancellationToken));

    /// <summary>Deactivate a module on this install (frees the licence to move to another install).</summary>
    [HttpPost("{slug}/deactivate")]
    public async Task<IActionResult> Deactivate(string slug, CancellationToken cancellationToken) =>
        NoContent(await Sender.Send(new DeactivateModuleCommand(slug), cancellationToken));
}

public sealed record ActivateModuleRequest(string LicenceKey);
