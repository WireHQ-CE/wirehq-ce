using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WireHQ.Api.Controllers;
using WireHQ.Application.Entitlements;

namespace WireHQ.Api.Controllers.V1;

/// <summary>
/// The public Marketplace module catalogue (docs/33 §5.2, ADR-048). Anonymous — the marketplace pages and the CE
/// Modules console both read the same kept-core manifest (name, version, docs/changelog anchors, tier, status,
/// delivery). Unlike the SaaS-only commerce/checkout controller (which is stripped from the CE), this ships in
/// <b>every</b> edition so a CE install can render the full catalogue and overlay its own activation state.
/// </summary>
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/marketplace")]
public sealed class MarketplaceModulesController : ApiControllerBase
{
    /// <summary>The public manifest for every backed Marketplace module. Returns display + lifecycle metadata only.</summary>
    [HttpGet("modules")]
    [AllowAnonymous]
    public async Task<IActionResult> Modules(CancellationToken cancellationToken) =>
        Ok(await Sender.Send(new PublicMarketplaceModulesQuery(), cancellationToken));
}
