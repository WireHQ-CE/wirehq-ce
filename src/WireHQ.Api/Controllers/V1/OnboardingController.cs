using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WireHQ.Api.Controllers;
using WireHQ.Application.Features.Onboarding;

namespace WireHQ.Api.Controllers.V1;

/// <summary>
/// The post-signup Welcome Wizard ("Tell us about your deployment"). Optional + skippable; collected per
/// org for segmentation. Org-scoped + audited; not gated on email verification so users can onboard first.
/// </summary>
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/onboarding")]
[Authorize]
public sealed class OnboardingController : ApiControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken cancellationToken) =>
        Ok(await Sender.Send(new GetOnboardingQuery(), cancellationToken));

    [HttpPut]
    public async Task<IActionResult> Save(SaveOnboardingRequest request, CancellationToken cancellationToken) =>
        NoContent(await Sender.Send(
            new SaveOnboardingCommand(
                request.CompanyName, request.CompanyWebsite, request.Industry,
                request.TeamSize, request.VpnUsers, request.CurrentVpnSolution, request.UseCase),
            cancellationToken));

    [HttpPost("skip")]
    public async Task<IActionResult> Skip(CancellationToken cancellationToken) =>
        NoContent(await Sender.Send(new SkipOnboardingCommand(), cancellationToken));
}

public sealed record SaveOnboardingRequest(
    string? CompanyName,
    string? CompanyWebsite,
    string? Industry,
    string? TeamSize,
    string? VpnUsers,
    string? CurrentVpnSolution,
    string? UseCase);
