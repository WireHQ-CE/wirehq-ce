using System.Globalization;

namespace WireHQ.Application.Updates;

/// <summary>
/// A minimal SemVer used only to decide "is the manifest's version newer than mine" (docs/30 U-5). Parses
/// <c>MAJOR.MINOR.PATCH</c> with an optional <c>-prerelease</c> and an optional <c>+build</c> metadata suffix.
/// Build metadata is ignored for precedence (so the install's build-stamped <c>0.40.0+abc123</c> compares as
/// <c>0.40.0</c>); a prerelease sorts <em>below</em> its release (<c>0.41.0-rc1 &lt; 0.41.0</c>), so an install
/// running an RC is correctly told the stable release is newer. Kept-core + unit-tested (the bug-prone half).
/// </summary>
public readonly struct SemVer : IComparable<SemVer>
{
    private SemVer(int major, int minor, int patch, string? prerelease)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        Prerelease = prerelease;
    }

    public int Major { get; }

    public int Minor { get; }

    public int Patch { get; }

    /// <summary>The prerelease identifier (e.g. <c>rc1</c>), or null for a stable release.</summary>
    public string? Prerelease { get; }

    public static bool TryParse(string? value, out SemVer version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var text = value.Trim();
        if (text.StartsWith('v') || text.StartsWith('V'))
        {
            text = text[1..];
        }

        // Strip build metadata (everything after '+') — ignored for precedence.
        var plus = text.IndexOf('+');
        if (plus >= 0)
        {
            text = text[..plus];
        }

        // Split off the prerelease (everything after the first '-').
        string? prerelease = null;
        var dash = text.IndexOf('-');
        if (dash >= 0)
        {
            prerelease = text[(dash + 1)..];
            text = text[..dash];
        }

        var parts = text.Split('.');
        if (parts.Length != 3
            || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var major)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minor)
            || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var patch))
        {
            return false;
        }

        version = new SemVer(major, minor, patch, string.IsNullOrEmpty(prerelease) ? null : prerelease);
        return true;
    }

    public int CompareTo(SemVer other)
    {
        var core = Major.CompareTo(other.Major);
        if (core != 0)
        {
            return core;
        }

        core = Minor.CompareTo(other.Minor);
        if (core != 0)
        {
            return core;
        }

        core = Patch.CompareTo(other.Patch);
        if (core != 0)
        {
            return core;
        }

        // Same core: a prerelease has LOWER precedence than the corresponding release (SemVer §11).
        if (Prerelease is null && other.Prerelease is null)
        {
            return 0;
        }

        if (Prerelease is null)
        {
            return 1; // I am the release, other is a prerelease → I am greater.
        }

        if (other.Prerelease is null)
        {
            return -1;
        }

        return string.CompareOrdinal(Prerelease, other.Prerelease);
    }

    public static bool operator <(SemVer a, SemVer b) => a.CompareTo(b) < 0;

    public static bool operator >(SemVer a, SemVer b) => a.CompareTo(b) > 0;

    public static bool operator <=(SemVer a, SemVer b) => a.CompareTo(b) <= 0;

    public static bool operator >=(SemVer a, SemVer b) => a.CompareTo(b) >= 0;

    public override string ToString() =>
        Prerelease is null ? $"{Major}.{Minor}.{Patch}" : $"{Major}.{Minor}.{Patch}-{Prerelease}";
}
