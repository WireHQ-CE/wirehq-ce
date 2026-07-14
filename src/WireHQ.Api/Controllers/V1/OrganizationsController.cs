using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WireHQ.Api.Controllers;
using WireHQ.Application.Features.Organizations.GetCurrentOrganization;
using WireHQ.Application.Features.Organizations.UpdateOrganization;

namespace WireHQ.Api.Controllers.V1;

[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/organizations")]
[Authorize]
public sealed class OrganizationsController : ApiControllerBase
{
    /// <summary>The active tenant (resolved from the token), with headline counts + business profile.</summary>
    [HttpGet("current")]
    public async Task<IActionResult> Current(CancellationToken cancellationToken) =>
        Ok(await Sender.Send(new GetCurrentOrganizationQuery(), cancellationToken));

    /// <summary>Update the active tenant's name + business-profile fields (Owner/Admin).</summary>
    [HttpPatch("current")]
    public async Task<IActionResult> UpdateCurrent(UpdateOrganizationRequest request, CancellationToken cancellationToken) =>
        NoContent(await Sender.Send(
            new UpdateOrganizationCommand(
                request.Name, request.LegalName, request.Website, request.Industry,
                request.CompanySize, request.Country, request.Timezone),
            cancellationToken));

    // NB: the billing-profile endpoints (GET/PUT current/billing) live in their own controller
    // file — a SaaS-only surface the Community Edition strip removes (docs/17 §5).
}

public sealed record UpdateOrganizationRequest(
    string Name,
    string? LegalName,
    string? Website,
    string? Industry,
    string? CompanySize,
    string? Country,
    string? Timezone);
