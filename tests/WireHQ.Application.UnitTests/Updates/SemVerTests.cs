using FluentAssertions;
using WireHQ.Application.Updates;
using Xunit;

namespace WireHQ.Application.UnitTests.Updates;

/// <summary>The SemVer compare that decides "is a newer version available" (docs/30 U-5) — the bug-prone half.</summary>
public sealed class SemVerTests
{
    [Theory]
    [InlineData("0.41.0", 0, 41, 0)]
    [InlineData("v0.41.0", 0, 41, 0)]
    [InlineData("1.2.3", 1, 2, 3)]
    [InlineData("0.40.0+2b0024d", 0, 40, 0)] // build metadata stripped
    public void Parses_valid_versions(string text, int major, int minor, int patch)
    {
        SemVer.TryParse(text, out var v).Should().BeTrue();
        (v.Major, v.Minor, v.Patch).Should().Be((major, minor, patch));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("0.41")]
    [InlineData("0.41.0.0")]
    [InlineData("latest")]
    [InlineData("0.x.0")]
    public void Rejects_malformed_versions(string? text)
    {
        SemVer.TryParse(text, out _).Should().BeFalse();
    }

    [Fact]
    public void Orders_by_major_then_minor_then_patch()
    {
        Newer("0.41.0", "0.40.0");
        Newer("0.40.1", "0.40.0");
        Newer("1.0.0", "0.99.99");
        Equal("0.40.0", "0.40.0");
    }

    [Fact]
    public void Build_metadata_is_ignored_for_precedence()
    {
        Equal("0.40.0+abc123", "0.40.0");
    }

    [Fact]
    public void A_prerelease_sorts_below_its_release()
    {
        // An install on an RC must be told the stable release is newer (docs/30 U-5).
        Newer("0.41.0", "0.41.0-rc1");
        Newer("0.41.0-rc2", "0.41.0-rc1");
    }

    private static void Newer(string greater, string lesser)
    {
        SemVer.TryParse(greater, out var g).Should().BeTrue();
        SemVer.TryParse(lesser, out var l).Should().BeTrue();
        (g > l).Should().BeTrue($"{greater} should be newer than {lesser}");
        (l < g).Should().BeTrue();
    }

    private static void Equal(string a, string b)
    {
        SemVer.TryParse(a, out var x).Should().BeTrue();
        SemVer.TryParse(b, out var y).Should().BeTrue();
        x.CompareTo(y).Should().Be(0);
    }
}
