using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WireHQ.Api.Controllers;
using WireHQ.Application.Features.Branding;
using WireHQ.Domain.Platform;

namespace WireHQ.Api.Controllers.V1;

/// <summary>
/// The install-global branding surface (docs/34). Operator endpoints (Super-Admin, enforced by the platform-request
/// pipeline) read/write the brand and manage the logo/favicon images; the anonymous <c>GET /api/v1/branding</c> is what
/// the browser shell reads pre-login; the anonymous, content-addressed <c>assets/{id}</c> serve is <b>hardened at the
/// API layer</b> (sandbox CSP + nosniff + canonical content type, never relying on nginx). Kept-core — the CE console
/// drives it once the <c>branding.basic</c> module is activated. (docs/34 §4, ADR-049, BR-16)
/// </summary>
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/branding")]
public sealed class BrandingController : ApiControllerBase
{
    /// <summary>The public brand config the shell renders pre-login (nulls ⇒ the shipped WireHQ brand).</summary>
    [HttpGet]
    [AllowAnonymous]
    public async Task<IActionResult> Public(CancellationToken cancellationToken)
    {
        var result = await Sender.Send(new PublicBrandingQuery(), cancellationToken);
        if (result.IsFailure)
        {
            return Problem(result.Error);
        }

        var b = result.Value;
        return Ok(new
        {
            b.ProductName,
            b.BrandColor,
            logoLightUrl = AssetUrl(b.LogoLightAssetId),
            logoDarkUrl = AssetUrl(b.LogoDarkAssetId),
            faviconUrl = AssetUrl(b.FaviconAssetId),
            b.BrandRevision,
        });
    }

    /// <summary>Serve a brand image by id — anonymous, content-addressed, and hardened against active-content abuse.</summary>
    [HttpGet("assets/{id:guid}")]
    [AllowAnonymous]
    public async Task<IActionResult> Asset(Guid id, CancellationToken cancellationToken)
    {
        var result = await Sender.Send(new GetBrandAssetQuery(id), cancellationToken);
        if (result.IsFailure)
        {
            return Problem(result.Error);
        }

        // Defense in depth (BR-16): a direct navigation to the asset is sandboxed + never MIME-sniffed; the content
        // type is the canonical one the domain validated, never an echoed upload header. Content-addressed ⇒ immutable.
        Response.Headers["Content-Security-Policy"] = "sandbox; default-src 'none'";
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        Response.Headers["Content-Disposition"] = "inline";
        Response.Headers["Cache-Control"] = "public, max-age=31536000, immutable";
        return File(result.Value.Bytes, result.Value.ContentType);
    }

    /// <summary>Read the current brand settings for the operator console (Super-Admin).</summary>
    [HttpGet("settings")]
    [Authorize]
    public async Task<IActionResult> Get(CancellationToken cancellationToken) =>
        Ok(await Sender.Send(new GetBrandingQuery(), cancellationToken));

    /// <summary>Update the product name + brand colour (Super-Admin).</summary>
    [HttpPut("settings")]
    [Authorize]
    public async Task<IActionResult> Update(UpdateBrandingRequest request, CancellationToken cancellationToken) =>
        Ok(await Sender.Send(new UpdateBrandingCommand(request.ProductName, request.BrandColor), cancellationToken));

    /// <summary>Upload (or replace) a brand image. Transport-capped early; the domain enforces ≤256 KB + PNG/ICO.</summary>
    [HttpPost("assets/{kind}")]
    [Authorize]
    [RequestSizeLimit(1_048_576)]
    public async Task<IActionResult> Upload(string kind, IFormFile file, CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<BrandAssetKind>(kind, ignoreCase: true, out var assetKind))
        {
            return BadRequest(new { error = "Unknown brand image slot." });
        }

        using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer, cancellationToken);

        var result = await Sender.Send(
            new UploadBrandAssetCommand(assetKind, file.ContentType, buffer.ToArray()), cancellationToken);
        return result.IsFailure ? Problem(result.Error) : Ok(new { assetId = result.Value });
    }

    /// <summary>Remove a brand image, reverting the slot to the shipped WireHQ mark (Super-Admin).</summary>
    [HttpDelete("assets/{kind}")]
    [Authorize]
    public async Task<IActionResult> Remove(string kind, CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<BrandAssetKind>(kind, ignoreCase: true, out var assetKind))
        {
            return BadRequest(new { error = "Unknown brand image slot." });
        }

        return NoContent(await Sender.Send(new RemoveBrandAssetCommand(assetKind), cancellationToken));
    }

    private static string? AssetUrl(Guid? id) => id is null ? null : $"/api/v1/branding/assets/{id}";
}

public sealed record UpdateBrandingRequest(string? ProductName, string? BrandColor);
