namespace WireHQ.Application.Updates;

/// <summary>
/// The pure, unit-tested decision (docs/30 U-5): given the install's running version and a signature-verified
/// manifest, is a newer version available, and how loud should it be? Owns the SemVer compare, the monotonic
/// anti-rollback floor (a manifest can never walk the install <em>backwards</em> to "up to date"), and the
/// below-min-supported tone. Kept-core so the main CI covers every edge (newer/older/equal/prerelease/malformed).
/// </summary>
public static class UpdateAdvisory
{
    // The "what's new" link base. The update banner is a self-hosted / Community-Edition feature, so it points at
    // the PUBLIC CE repo's release tags. Config-overridable (Updates:ReleaseNotesBaseUrl, passed to Evaluate) so it
    // follows the repo wherever CE releases actually live — no code change when the repo moves. The link is always
    // CONSTRUCTED here, never taken from the (untrusted) manifest (docs/30 U-4).
    public const string DefaultReleaseTagBase = "https://github.com/WireHQ-CE/wirehq-ce/releases/tag/v";

    /// <summary>
    /// Monotonically latches the LOUDEST advisory for the highest version seen (docs/30 U-4). A replayed older /
    /// less-severe (but validly-signed) manifest must never downgrade a security banner already shown, so the
    /// version is taken as the higher of the two and every loudness signal is sticky (security OR, severity max,
    /// requires-migration OR, higher min-supported). Stickiness clears naturally once the install upgrades — then
    /// <see cref="Evaluate"/> returns up-to-date and ignores it. The higher-version manifest supplies the version /
    /// release date / summary; the loudness is the merge. Kept-core + unit-tested.
    /// </summary>
    public static UpdateManifest Latch(UpdateManifest? latched, UpdateManifest incoming)
    {
        ArgumentNullException.ThrowIfNull(incoming);
        if (latched is null)
        {
            return incoming;
        }

        var higher = SemVer.TryParse(latched.LatestVersion, out var lat)
            && SemVer.TryParse(incoming.LatestVersion, out var inc) && lat > inc
            ? latched
            : incoming;

        return higher with
        {
            Security = latched.Security || incoming.Security,
            Severity = (UpdateSeverity)Math.Max((int)latched.Severity, (int)incoming.Severity),
            RequiresMigration = latched.RequiresMigration || incoming.RequiresMigration,
            MinSupportedVersion = HigherMinSupported(latched.MinSupportedVersion, incoming.MinSupportedVersion),
        };
    }

    private static string? HigherMinSupported(string? a, string? b)
    {
        if (!SemVer.TryParse(a, out var va))
        {
            return b;
        }

        if (!SemVer.TryParse(b, out var vb))
        {
            return a;
        }

        return va >= vb ? a : b;
    }

    /// <summary>
    /// Evaluate the advisory. <paramref name="highestSeenVersion"/> is an optional anti-rollback floor (the
    /// provider now latches at the manifest level via <see cref="Latch"/>, so it usually passes null). Returns
    /// <see cref="UpdateStatus.Unknown"/> when either version is unparseable (a bad manifest can't produce a false
    /// all-clear).
    /// </summary>
    public static UpdateStatus Evaluate(
        string currentVersion, UpdateManifest manifest, DateTimeOffset checkedAtUtc,
        string? highestSeenVersion = null, string? releaseUrlBase = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        if (!SemVer.TryParse(currentVersion, out var current) || !SemVer.TryParse(manifest.LatestVersion, out var latest))
        {
            return UpdateStatus.Unknown(currentVersion);
        }

        // Anti-rollback floor: never compare against a version lower than the highest we have already seen, so a
        // replayed older (but validly-signed) manifest cannot suppress a known update (docs/30 U-4).
        var targetText = manifest.LatestVersion;
        if (SemVer.TryParse(highestSeenVersion, out var floor) && floor > latest)
        {
            latest = floor;
            targetText = highestSeenVersion!;
        }

        if (current >= latest)
        {
            return new UpdateStatus(
                UpdateState.UpToDate, current.ToString(), targetText, false, UpdateSeverity.None,
                false, false, null, null, checkedAtUtc);
        }

        var unsupported = SemVer.TryParse(manifest.MinSupportedVersion, out var minSupported) && current < minSupported;

        return new UpdateStatus(
            UpdateState.UpdateAvailable,
            current.ToString(),
            targetText,
            manifest.Security,
            manifest.Severity,
            unsupported,
            manifest.RequiresMigration,
            manifest.Summary,
            (string.IsNullOrWhiteSpace(releaseUrlBase) ? DefaultReleaseTagBase : releaseUrlBase) + targetText,
            checkedAtUtc);
    }
}
