using WireHQ.Domain.Common;
using WireHQ.Shared.Results;

namespace WireHQ.Domain.Platform;

/// <summary>Which brand image a <see cref="BrandAsset"/> holds.</summary>
public enum BrandAssetKind
{
    LogoLight = 0,
    LogoDark = 1,
    Favicon = 2,
}

/// <summary>Stable, machine-readable branding errors (mapped centrally to HTTP).</summary>
public static class BrandingErrors
{
    public static readonly Error AssetEmpty = Error.Validation("branding.asset.empty", "The uploaded file is empty.");
    public static readonly Error AssetTooLarge = Error.Validation("branding.asset.too_large", "Brand images must be 256 KB or smaller.");
    public static readonly Error AssetUnsupportedType = Error.Validation("branding.asset.unsupported_type", "Only PNG or ICO images are accepted.");
    public static readonly Error AssetContentMismatch = Error.Validation("branding.asset.content_mismatch", "The file content does not match its declared image type.");
    public static readonly Error InvalidColor = Error.Validation("branding.invalid_color", "Brand colour must be a 6-digit hex value like #1a2b3c.");
}

/// <summary>
/// An operator brand image (logo light/dark or favicon), stored as a bounded blob. **Install-global** (platform-wide,
/// NOT tenant-owned — one brand per install; docs/34 §4.3) so it stays out of the RLS tenant loop and lives in the
/// <c>core</c> schema. **PNG/ICO only** — SVG is a stored-XSS surface and is deliberately excluded (docs/34 §0/BR-11),
/// mirroring the avatar rules. Served from a hardened, sandboxed, content-addressed endpoint (a fresh id is minted on
/// every replace, so the URL is immutable-cacheable).
/// </summary>
public sealed class BrandAsset : Entity
{
    /// <summary>The hard ceiling for a brand image (256 KB). Brand marks are small; this blunts blob/DoS abuse.</summary>
    public const int MaxBytes = 256 * 1024;

    private const string Png = "image/png";
    private const string Ico = "image/x-icon";

    // EF Core
    private BrandAsset()
    {
        ContentType = null!;
        Bytes = null!;
    }

    private BrandAsset(Guid id, BrandAssetKind kind, string contentType, byte[] bytes, DateTimeOffset now)
        : base(id)
    {
        Kind = kind;
        ContentType = contentType;
        Bytes = bytes;
        UpdatedAtUtc = now;
    }

    public BrandAssetKind Kind { get; private set; }

    /// <summary>The canonical content type to serve with (derived from the validated bytes, never the echoed upload).</summary>
    public string ContentType { get; private set; }

    public byte[] Bytes { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    /// <summary>
    /// Validate and create a brand asset. Guards (defense in depth over the command validator): non-empty, ≤256 KB,
    /// a PNG/ICO declared content type, AND magic bytes that match — so a mislabelled or active-content file (e.g. an
    /// SVG renamed to .png) is rejected. The stored <see cref="ContentType"/> is the canonical type inferred from the
    /// bytes, so the serve endpoint never echoes an attacker-supplied type.
    /// </summary>
    public static Result<BrandAsset> Create(BrandAssetKind kind, string? declaredContentType, byte[]? bytes, DateTimeOffset now)
    {
        if (bytes is null || bytes.Length == 0)
        {
            return BrandingErrors.AssetEmpty;
        }

        if (bytes.Length > MaxBytes)
        {
            return BrandingErrors.AssetTooLarge;
        }

        var declared = declaredContentType?.Trim().ToLowerInvariant();
        var declaredIsPng = declared == Png;
        var declaredIsIco = declared is Ico or "image/vnd.microsoft.icon";
        if (!declaredIsPng && !declaredIsIco)
        {
            return BrandingErrors.AssetUnsupportedType;
        }

        // Magic-byte sniff — the canonical type comes from the bytes, not the declared header.
        string canonical;
        if (IsPng(bytes))
        {
            canonical = Png;
        }
        else if (IsIco(bytes))
        {
            canonical = Ico;
        }
        else
        {
            return BrandingErrors.AssetContentMismatch;
        }

        // The declared type must agree with what the bytes actually are.
        if ((declaredIsPng && canonical != Png) || (declaredIsIco && canonical != Ico))
        {
            return BrandingErrors.AssetContentMismatch;
        }

        return new BrandAsset(Guid.CreateVersion7(), kind, canonical, bytes, now);
    }

    // PNG signature: 89 50 4E 47 0D 0A 1A 0A.
    private static bool IsPng(byte[] b) =>
        b.Length >= 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47
        && b[4] == 0x0D && b[5] == 0x0A && b[6] == 0x1A && b[7] == 0x0A;

    // ICO header: 00 00 01 00.
    private static bool IsIco(byte[] b) =>
        b.Length >= 4 && b[0] == 0x00 && b[1] == 0x00 && b[2] == 0x01 && b[3] == 0x00;
}
