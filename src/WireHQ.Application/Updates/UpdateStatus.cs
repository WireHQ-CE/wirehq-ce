using System.Text.Json.Serialization;

namespace WireHQ.Application.Updates;

/// <summary>Whether a newer version is known. <see cref="Unknown"/> (no successful check yet) is NEVER shown as
/// "up to date" — the UI renders it as "checking…" so a blocked poll can't read as a false all-clear (docs/30 U-7).
/// Serialized as its string name so the frontend compares strings, not integers.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum UpdateState
{
    Unknown,
    UpToDate,
    UpdateAvailable,
}

/// <summary>
/// The install's update situation, as read by <c>GET /api/v1/updates/status</c> and rendered by the operator's
/// banner/modal (docs/30 U-7/U-8). The release link is CONSTRUCTED here from the version (never trusted from the
/// manifest); the command is fixed in the UI. <see cref="Summary"/> is the manifest's optional untrusted note.
/// </summary>
public sealed record UpdateStatus(
    UpdateState State,
    string CurrentVersion,
    string? LatestVersion,
    bool Security,
    UpdateSeverity Severity,
    bool Unsupported,
    bool RequiresMigration,
    string? Summary,
    string? ReleaseUrl,
    DateTimeOffset? CheckedAtUtc)
{
    /// <summary>Not checked yet (or the current version is unparseable) — render "checking…", never "up to date".</summary>
    public static UpdateStatus Unknown(string currentVersion) =>
        new(UpdateState.Unknown, currentVersion, null, false, UpdateSeverity.None, false, false, null, null, null);
}
