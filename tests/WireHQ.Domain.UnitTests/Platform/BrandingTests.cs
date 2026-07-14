using FluentAssertions;
using WireHQ.Domain.Platform;
using Xunit;

namespace WireHQ.Domain.UnitTests.Platform;

/// <summary>
/// The install-global branding domain (docs/34). Covers the settings mutators (product name + validated colour +
/// revision bump) and the <see cref="BrandAsset"/> upload guards (size, PNG/ICO type, magic-byte content check) — the
/// defense-in-depth the design review required (docs/34 §0/BR-9/BR-11).
/// </summary>
public sealed class BrandingTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 12, 0, 0, 0, TimeSpan.Zero);

    private static byte[] PngBytes(int size = 64)
    {
        var b = new byte[size];
        byte[] header = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        header.CopyTo(b, 0);
        return b;
    }

    private static byte[] IcoBytes(int size = 64)
    {
        var b = new byte[size];
        byte[] header = [0x00, 0x00, 0x01, 0x00];
        header.CopyTo(b, 0);
        return b;
    }

    [Fact]
    public void SetBranding_stores_name_and_normalises_colour_and_bumps_revision()
    {
        var settings = PlatformSettings.CreateDefault();

        var result = settings.SetBranding("Acme VPN", "#1A2B3C");

        result.IsSuccess.Should().BeTrue();
        settings.ProductName.Should().Be("Acme VPN");
        settings.BrandColor.Should().Be("#1a2b3c");
        settings.BrandRevision.Should().Be(1);
        settings.BrandingConfigured.Should().BeTrue();
    }

    [Fact]
    public void SetBranding_blank_values_clear_back_to_default()
    {
        var settings = PlatformSettings.CreateDefault();
        settings.SetBranding("Acme", "#abcdef");

        settings.SetBranding("  ", "");

        settings.ProductName.Should().BeNull();
        settings.BrandColor.Should().BeNull();
        settings.BrandingConfigured.Should().BeFalse();
        settings.BrandRevision.Should().Be(2);
    }

    [Theory]
    [InlineData("1a2b3c")]      // missing '#'
    [InlineData("#12345")]      // too short
    [InlineData("#1234567")]    // too long
    [InlineData("#12345g")]     // non-hex
    [InlineData("red")]
    public void SetBranding_rejects_an_invalid_colour(string bad)
    {
        var settings = PlatformSettings.CreateDefault();

        var result = settings.SetBranding("Acme", bad);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be(BrandingErrors.InvalidColor);
        settings.BrandColor.Should().BeNull();
        settings.BrandRevision.Should().Be(0); // no change on rejection
    }

    [Fact]
    public void SetBrandAsset_sets_the_right_slot_and_bumps_revision()
    {
        var settings = PlatformSettings.CreateDefault();
        var id = Guid.NewGuid();

        settings.SetBrandAsset(BrandAssetKind.LogoDark, id);

        settings.LogoDarkAssetId.Should().Be(id);
        settings.LogoLightAssetId.Should().BeNull();
        settings.BrandRevision.Should().Be(1);

        settings.SetBrandAsset(BrandAssetKind.LogoDark, null);
        settings.LogoDarkAssetId.Should().BeNull();
        settings.BrandRevision.Should().Be(2);
    }

    [Fact]
    public void BrandAsset_accepts_a_valid_png_and_stores_the_canonical_type()
    {
        var result = BrandAsset.Create(BrandAssetKind.LogoLight, "image/png", PngBytes(), Now);

        result.IsSuccess.Should().BeTrue();
        result.Value.Kind.Should().Be(BrandAssetKind.LogoLight);
        result.Value.ContentType.Should().Be("image/png");
        result.Value.Bytes.Should().HaveCount(64);
    }

    [Fact]
    public void BrandAsset_accepts_an_ico_favicon()
    {
        var result = BrandAsset.Create(BrandAssetKind.Favicon, "image/vnd.microsoft.icon", IcoBytes(), Now);

        result.IsSuccess.Should().BeTrue();
        result.Value.ContentType.Should().Be("image/x-icon");
    }

    [Fact]
    public void BrandAsset_rejects_empty()
    {
        BrandAsset.Create(BrandAssetKind.LogoLight, "image/png", [], Now)
            .Error.Should().Be(BrandingErrors.AssetEmpty);
    }

    [Fact]
    public void BrandAsset_rejects_oversized()
    {
        BrandAsset.Create(BrandAssetKind.LogoLight, "image/png", PngBytes(BrandAsset.MaxBytes + 1), Now)
            .Error.Should().Be(BrandingErrors.AssetTooLarge);
    }

    [Theory]
    [InlineData("image/svg+xml")]
    [InlineData("image/jpeg")]
    [InlineData("text/html")]
    public void BrandAsset_rejects_an_unsupported_type(string type)
    {
        BrandAsset.Create(BrandAssetKind.LogoLight, type, PngBytes(), Now)
            .Error.Should().Be(BrandingErrors.AssetUnsupportedType);
    }

    [Fact]
    public void BrandAsset_rejects_content_that_does_not_match_its_declared_type()
    {
        // Declared PNG, but the bytes are an ICO — an SVG-renamed-to-.png attack fails the same way.
        BrandAsset.Create(BrandAssetKind.LogoLight, "image/png", IcoBytes(), Now)
            .Error.Should().Be(BrandingErrors.AssetContentMismatch);
    }

    [Fact]
    public void BrandAsset_rejects_bytes_that_are_no_known_image()
    {
        BrandAsset.Create(BrandAssetKind.LogoLight, "image/png", new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, Now)
            .Error.Should().Be(BrandingErrors.AssetContentMismatch);
    }
}
