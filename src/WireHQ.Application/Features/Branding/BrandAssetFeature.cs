using Microsoft.EntityFrameworkCore;
using WireHQ.Application.Abstractions;
using WireHQ.Application.Abstractions.Persistence;
using WireHQ.Application.Authorization;
using WireHQ.Application.Common.Messaging;
using WireHQ.Application.Entitlements;
using WireHQ.Domain.Auditing;
using WireHQ.Domain.Platform;
using WireHQ.Shared.Results;

namespace WireHQ.Application.Features.Branding;

// Brand image upload / removal / serve (docs/34 §4.3). Super-Admin write; anonymous, content-addressed read. Assets
// are bounded PNG/ICO blobs validated in the domain (BrandAsset.Create). A replace mints a fresh id and garbage-
// collects the old row, so the serve URL is immutable-cacheable. The hardened response headers
// (Content-Disposition/CSP-sandbox/nosniff) are set by BrandingController, not here.

/// <summary>Upload (or replace) a brand image. Returns the new asset id.</summary>
public sealed record UploadBrandAssetCommand(BrandAssetKind Kind, string? ContentType, byte[] Bytes)
    : ICommand<Guid>, IAuthorizedRequest, IRequiresFeature
{
    public IReadOnlyCollection<string> RequiredPermissions => [Permissions.Branding.Manage];
    public string RequiredFeature => PlanFeatures.Branding;
}

public sealed class UploadBrandAssetCommandHandler(IApplicationDbContext db, IDateTimeProvider clock, IAuditWriter audit)
    : ICommandHandler<UploadBrandAssetCommand, Guid>
{
    public async Task<Result<Guid>> Handle(UploadBrandAssetCommand command, CancellationToken cancellationToken)
    {
        var created = BrandAsset.Create(command.Kind, command.ContentType, command.Bytes, clock.UtcNow);
        if (created.IsFailure)
        {
            return created.Error;
        }

        var settings = await db.PlatformSettings.FirstOrDefaultAsync(cancellationToken);
        if (settings is null)
        {
            return Error.NotFound("branding.settings_not_found", "Platform settings were not found.");
        }

        var asset = created.Value;
        var previousId = PreviousAssetId(settings, command.Kind);

        db.BrandAssets.Add(asset);
        settings.SetBrandAsset(command.Kind, asset.Id);
        await GarbageCollectAsync(previousId, cancellationToken);

        audit.Record("branding.asset.uploaded", AuditOutcome.Success, nameof(BrandAsset), asset.Id.ToString(),
            new { command.Kind, asset.ContentType, Bytes = command.Bytes.Length });

        return asset.Id;
    }

    private async Task GarbageCollectAsync(Guid? previousId, CancellationToken cancellationToken)
    {
        if (previousId is null)
        {
            return;
        }

        var old = await db.BrandAssets.FirstOrDefaultAsync(a => a.Id == previousId, cancellationToken);
        if (old is not null)
        {
            db.BrandAssets.Remove(old);
        }
    }

    internal static Guid? PreviousAssetId(PlatformSettings s, BrandAssetKind kind) => kind switch
    {
        BrandAssetKind.LogoLight => s.LogoLightAssetId,
        BrandAssetKind.LogoDark => s.LogoDarkAssetId,
        BrandAssetKind.Favicon => s.FaviconAssetId,
        _ => null,
    };
}

/// <summary>Remove a brand image, reverting the slot to the shipped WireHQ mark.</summary>
public sealed record RemoveBrandAssetCommand(BrandAssetKind Kind) : ICommand, IAuthorizedRequest, IRequiresFeature
{
    public IReadOnlyCollection<string> RequiredPermissions => [Permissions.Branding.Manage];
    public string RequiredFeature => PlanFeatures.Branding;
}

public sealed class RemoveBrandAssetCommandHandler(IApplicationDbContext db, IAuditWriter audit)
    : ICommandHandler<RemoveBrandAssetCommand>
{
    public async Task<Result> Handle(RemoveBrandAssetCommand command, CancellationToken cancellationToken)
    {
        var settings = await db.PlatformSettings.FirstOrDefaultAsync(cancellationToken);
        if (settings is null)
        {
            return Error.NotFound("branding.settings_not_found", "Platform settings were not found.");
        }

        var currentId = UploadBrandAssetCommandHandler.PreviousAssetId(settings, command.Kind);
        settings.SetBrandAsset(command.Kind, null);

        if (currentId is not null)
        {
            var asset = await db.BrandAssets.FirstOrDefaultAsync(a => a.Id == currentId, cancellationToken);
            if (asset is not null)
            {
                db.BrandAssets.Remove(asset);
            }
        }

        audit.Record("branding.asset.removed", AuditOutcome.Success, nameof(BrandAsset), currentId?.ToString() ?? "-",
            new { command.Kind });

        return Result.Success();
    }
}

/// <summary>The bytes + canonical content type of a brand image, for the hardened serve endpoint.</summary>
public sealed record BrandAssetContent(byte[] Bytes, string ContentType);

/// <summary>Read a brand image by id — anonymous + org-less (a public, content-addressed asset).</summary>
public sealed record GetBrandAssetQuery(Guid Id) : IQuery<BrandAssetContent>, ITenantUnscopedRequest;

public sealed class GetBrandAssetQueryHandler(IApplicationDbContext db)
    : IQueryHandler<GetBrandAssetQuery, BrandAssetContent>
{
    public async Task<Result<BrandAssetContent>> Handle(GetBrandAssetQuery query, CancellationToken cancellationToken)
    {
        var asset = await db.BrandAssets.AsNoTracking().FirstOrDefaultAsync(a => a.Id == query.Id, cancellationToken);
        return asset is null
            ? Error.NotFound("branding.asset.not_found", "Brand image was not found.")
            : new BrandAssetContent(asset.Bytes, asset.ContentType);
    }
}
