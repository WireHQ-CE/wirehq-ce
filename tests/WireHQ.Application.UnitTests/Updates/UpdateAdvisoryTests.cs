using FluentAssertions;
using WireHQ.Application.Updates;
using Xunit;

namespace WireHQ.Application.UnitTests.Updates;

/// <summary>The update advisory decision (docs/30 U-5): newer/older/equal, security loudness, below-min, the
/// anti-rollback floor, and malformed-input → Unknown (never a false all-clear).</summary>
public sealed class UpdateAdvisoryTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 20, 0, 0, 0, TimeSpan.Zero);

    private static UpdateManifest Manifest(
        string latest, bool security = false, UpdateSeverity severity = UpdateSeverity.None,
        bool requiresMigration = false, string? minSupported = null, string? summary = null) =>
        new()
        {
            LatestVersion = latest,
            Security = security,
            Severity = severity,
            RequiresMigration = requiresMigration,
            MinSupportedVersion = minSupported,
            Summary = summary,
        };

    [Fact]
    public void Equal_versions_are_up_to_date()
    {
        var status = UpdateAdvisory.Evaluate("0.40.0", Manifest("0.40.0"), Now);
        status.State.Should().Be(UpdateState.UpToDate);
    }

    [Fact]
    public void A_newer_install_than_the_manifest_is_up_to_date()
    {
        // Dev/nightly ahead of the released manifest — never a phantom "update available".
        UpdateAdvisory.Evaluate("0.41.0", Manifest("0.40.0"), Now).State.Should().Be(UpdateState.UpToDate);
    }

    [Fact]
    public void A_newer_manifest_yields_an_update_with_a_constructed_release_url()
    {
        var status = UpdateAdvisory.Evaluate("0.40.0+abc123", Manifest("0.41.0", requiresMigration: true), Now);

        status.State.Should().Be(UpdateState.UpdateAvailable);
        status.LatestVersion.Should().Be("0.41.0");
        status.RequiresMigration.Should().BeTrue();
        status.ReleaseUrl.Should().Be("https://github.com/WireHQ-CE/wirehq-ce/releases/tag/v0.41.0");
        status.CheckedAtUtc.Should().Be(Now);
    }

    [Fact]
    public void Security_and_severity_flow_through_for_a_newer_release()
    {
        var status = UpdateAdvisory.Evaluate(
            "0.40.0", Manifest("0.41.0", security: true, severity: UpdateSeverity.High, summary: "fix"), Now);

        status.Security.Should().BeTrue();
        status.Severity.Should().Be(UpdateSeverity.High);
        status.Summary.Should().Be("fix");
    }

    [Fact]
    public void Below_min_supported_is_flagged_unsupported()
    {
        var status = UpdateAdvisory.Evaluate("0.20.0", Manifest("0.41.0", minSupported: "0.30.0"), Now);

        status.State.Should().Be(UpdateState.UpdateAvailable);
        status.Unsupported.Should().BeTrue();
    }

    [Fact]
    public void A_replayed_older_manifest_cannot_walk_the_install_back_to_up_to_date()
    {
        // Install on 0.40.0 has already seen 0.41.0; an attacker replays an old signed manifest claiming 0.40.0.
        var status = UpdateAdvisory.Evaluate("0.40.0", Manifest("0.40.0"), Now, highestSeenVersion: "0.41.0");

        status.State.Should().Be(UpdateState.UpdateAvailable);
        status.LatestVersion.Should().Be("0.41.0");
    }

    [Theory]
    [InlineData("not-a-version", "0.41.0")]
    [InlineData("0.40.0", "garbage")]
    public void Unparseable_versions_are_unknown_not_a_false_all_clear(string current, string latest)
    {
        UpdateAdvisory.Evaluate(current, Manifest(latest), Now).State.Should().Be(UpdateState.Unknown);
    }

    [Fact]
    public void Latch_keeps_the_loudest_advisory_across_a_replayed_non_security_manifest()
    {
        // The install saw a critical security release, then an attacker replays the older, non-security manifest.
        var secure = Manifest("0.41.0", security: true, severity: UpdateSeverity.Critical);
        var replayedOlder = Manifest("0.40.0", security: false, severity: UpdateSeverity.None);

        var latched = UpdateAdvisory.Latch(secure, replayedOlder);

        latched.LatestVersion.Should().Be("0.41.0");
        latched.Security.Should().BeTrue("the security signal must not be downgraded by a replay");
        latched.Severity.Should().Be(UpdateSeverity.Critical);

        var status = UpdateAdvisory.Evaluate("0.40.0", latched, Now);
        status.Security.Should().BeTrue();
        status.Severity.Should().Be(UpdateSeverity.Critical);
    }

    [Fact]
    public void Latch_adopts_a_newer_version_but_keeps_the_missing_security_fix_loud()
    {
        var secure41 = Manifest("0.41.0", security: true, severity: UpdateSeverity.High);
        var routine42 = Manifest("0.42.0");

        var latched = UpdateAdvisory.Latch(secure41, routine42);

        latched.LatestVersion.Should().Be("0.42.0");
        latched.Security.Should().BeTrue("the install still lacks the 0.41.0 security fix");
    }

    [Fact]
    public void Latch_clears_once_the_install_has_upgraded_past_the_latched_version()
    {
        var latched = UpdateAdvisory.Latch(null, Manifest("0.41.0", security: true, severity: UpdateSeverity.Critical));
        UpdateAdvisory.Evaluate("0.41.0", latched, Now).State.Should().Be(UpdateState.UpToDate);
    }
}
