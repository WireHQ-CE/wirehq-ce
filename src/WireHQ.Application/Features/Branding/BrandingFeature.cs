using FluentValidation;
using Microsoft.EntityFrameworkCore;
using WireHQ.Application.Abstractions;
using WireHQ.Application.Abstractions.Persistence;
using WireHQ.Application.Authorization;
using WireHQ.Application.Common.Messaging;
using WireHQ.Application.Entitlements;
using WireHQ.Domain.Auditing;
using WireHQ.Domain.Organizations;
using WireHQ.Domain.Platform;
using WireHQ.Shared.Results;

namespace WireHQ.Application.Features.Branding;

// Operator branding config (docs/34) — the install-global product name + brand colour. Super-Admin read/write
// (IPlatformRequest); logo/favicon images are handled by BrandAssetFeature. The public, anonymous read
// (PublicBrandingQuery) is what the browser shell fetches pre-login: it is gated on the branding.basic entitlement via
// the install-global activated-module union (edition = CommunityEdition — no base plan grants branding, so an
// unentitled install, including every SaaS org, resolves to the shipped WireHQ brand). This whole area is kept-core.

/// <summary>The operator's brand override (as stored). Null fields fall back to the shipped WireHQ brand.</summary>
public sealed record BrandingSettingsDto(
    string? ProductName,
    string? BrandColor,
    Guid? LogoLightAssetId,
    Guid? LogoDarkAssetId,
    Guid? FaviconAssetId,
    int BrandRevision);

/// <summary>Read the current brand settings for the operator console.</summary>
public sealed record GetBrandingQuery : IQuery<BrandingSettingsDto>, IAuthorizedRequest, IRequiresFeature
{
    public IReadOnlyCollection<string> RequiredPermissions => [Permissions.Branding.Manage];
    public string RequiredFeature => PlanFeatures.Branding;
}

public sealed class GetBrandingQueryHandler(IApplicationDbContext db)
    : IQueryHandler<GetBrandingQuery, BrandingSettingsDto>
{
    public async Task<Result<BrandingSettingsDto>> Handle(GetBrandingQuery query, CancellationToken cancellationToken)
    {
        var s = await db.PlatformSettings.AsNoTracking().FirstOrDefaultAsync(cancellationToken);
        return Map(s);
    }

    internal static BrandingSettingsDto Map(PlatformSettings? s) => s is null
        ? new BrandingSettingsDto(null, null, null, null, null, 0)
        : new BrandingSettingsDto(s.ProductName, s.BrandColor, s.LogoLightAssetId, s.LogoDarkAssetId, s.FaviconAssetId, s.BrandRevision);
}

/// <summary>Update the operator product name + brand colour.</summary>
public sealed record UpdateBrandingCommand(string? ProductName, string? BrandColor)
    : ICommand<BrandingSettingsDto>, IAuthorizedRequest, IRequiresFeature
{
    public IReadOnlyCollection<string> RequiredPermissions => [Permissions.Branding.Manage];
    public string RequiredFeature => PlanFeatures.Branding;
}

public sealed class UpdateBrandingCommandValidator : AbstractValidator<UpdateBrandingCommand>
{
    public UpdateBrandingCommandValidator()
    {
        RuleFor(x => x.ProductName).MaximumLength(64);
        RuleFor(x => x.BrandColor)
            .Matches("^#[0-9A-Fa-f]{6}$")
            .When(x => !string.IsNullOrWhiteSpace(x.BrandColor))
            .WithMessage("Brand colour must be a 6-digit hex value like #1a2b3c.");
    }
}

public sealed class UpdateBrandingCommandHandler(IApplicationDbContext db, IAuditWriter audit)
    : ICommandHandler<UpdateBrandingCommand, BrandingSettingsDto>
{
    public async Task<Result<BrandingSettingsDto>> Handle(UpdateBrandingCommand command, CancellationToken cancellationToken)
    {
        var s = await db.PlatformSettings.FirstOrDefaultAsync(cancellationToken);
        if (s is null)
        {
            return Error.NotFound("branding.settings_not_found", "Platform settings were not found.");
        }

        var result = s.SetBranding(command.ProductName, command.BrandColor);
        if (result.IsFailure)
        {
            return result.Error;
        }

        audit.Record("branding.updated", AuditOutcome.Success, nameof(PlatformSettings), s.Id.ToString(),
            new { command.ProductName, command.BrandColor });

        return GetBrandingQueryHandler.Map(s);
    }
}

/// <summary>The public brand config the browser shell reads pre-login. Null fields ⇒ the shipped WireHQ brand.</summary>
public sealed record PublicBrandingDto(
    string? ProductName,
    string? BrandColor,
    Guid? LogoLightAssetId,
    Guid? LogoDarkAssetId,
    Guid? FaviconAssetId,
    int BrandRevision);

/// <summary>Read the public brand config — anonymous + org-less, entitlement-gated, WireHQ-default fallback.</summary>
public sealed record PublicBrandingQuery : IQuery<PublicBrandingDto>, ITenantUnscopedRequest;

public sealed class PublicBrandingQueryHandler(IApplicationDbContext db, IEffectiveFeatureSet features)
    : IQueryHandler<PublicBrandingQuery, PublicBrandingDto>
{
    private static readonly PublicBrandingDto Default = new(null, null, null, null, null, 0);

    public async Task<Result<PublicBrandingDto>> Handle(PublicBrandingQuery query, CancellationToken cancellationToken)
    {
        // Install-global entitlement: only the activated-module union can add branding.basic (no base plan has it),
        // so an unentitled install (incl. every SaaS org) resolves to the shipped WireHQ brand.
        var entitled = await features.HasFeatureAsync(OrganizationEdition.CommunityEdition, PlanFeatures.Branding, cancellationToken);
        if (!entitled)
        {
            return Default;
        }

        var s = await db.PlatformSettings.AsNoTracking().FirstOrDefaultAsync(cancellationToken);
        return s is null
            ? Default
            : new PublicBrandingDto(s.ProductName, s.BrandColor, s.LogoLightAssetId, s.LogoDarkAssetId, s.FaviconAssetId, s.BrandRevision);
    }
}
