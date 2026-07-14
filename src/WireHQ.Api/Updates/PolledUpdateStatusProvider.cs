using Microsoft.Extensions.Configuration;
using WireHQ.Application.Abstractions;
using WireHQ.Application.Updates;

namespace WireHQ.Api.Updates;

/// <summary>
/// The Community Edition <see cref="IUpdateStatusProvider"/> (docs/30 U-6/U-7): holds the last signature-verified
/// manifest written by <see cref="UpdateCheckHostedService"/> and evaluates the advisory on demand against the
/// caller's build-stamped version. A <b>singleton</b> with an immutable snapshot swapped under a lock (a scoped
/// lifetime would lose the polled state between requests). Before the first successful poll it reports
/// <see cref="UpdateState.Unknown"/> — never a false "up to date". Tracks the highest version ever seen as an
/// in-memory anti-rollback floor. CE-ONLY (overlay-added).
/// </summary>
public sealed class PolledUpdateStatusProvider(IDateTimeProvider clock, IConfiguration configuration) : IUpdateStatusProvider
{
    private readonly Lock _gate = new();
    private Snapshot? _snapshot;

    public UpdateStatus Current(string currentVersion)
    {
        Snapshot? snapshot;
        lock (_gate)
        {
            snapshot = _snapshot;
        }

        return snapshot is null
            ? UpdateStatus.Unknown(currentVersion)
            : UpdateAdvisory.Evaluate(
                currentVersion, snapshot.Latched, snapshot.CheckedAtUtc,
                releaseUrlBase: configuration["Updates:ReleaseNotesBaseUrl"]);
    }

    /// <summary>
    /// Records a freshly-verified manifest, monotonically latching the loudest advisory for the highest version
    /// seen (docs/30 U-4) so a replayed older/less-severe signed manifest can't downgrade a security banner.
    /// Called by the poller. (In-memory for v1; a persisted store across restarts is a documented fast-follow, U-13.)
    /// </summary>
    public void Record(UpdateManifest manifest)
    {
        var now = clock.UtcNow;
        lock (_gate)
        {
            _snapshot = new Snapshot(UpdateAdvisory.Latch(_snapshot?.Latched, manifest), now);
        }
    }

    private sealed record Snapshot(UpdateManifest Latched, DateTimeOffset CheckedAtUtc);
}
