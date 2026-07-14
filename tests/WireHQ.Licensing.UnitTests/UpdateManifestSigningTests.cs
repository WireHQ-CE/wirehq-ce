using FluentAssertions;
using WireHQ.Application.Updates;
using Xunit;

namespace WireHQ.Licensing.UnitTests;

/// <summary>
/// The update-manifest producer/consumer round-trip (docs/30 §5): WireHQ signs a manifest offline with the
/// dedicated update key's private seed (the <c>--sign-update-manifest</c> release step), and the Community Edition
/// verifies it against the pinned public key baked into its image. Proves the signed manifest round-trips to all
/// its fields, that a foreign-signed manifest is rejected (never a false all-clear), and that the key pair printed
/// by the ceremony (<c>--generate-update-key</c>) actually signs + verifies.
/// </summary>
public sealed class UpdateManifestSigningTests
{
    private static UpdateManifest SampleManifest() => new()
    {
        LatestVersion = "0.41.0",
        ReleasedAtUtc = new DateTimeOffset(2026, 7, 20, 0, 0, 0, TimeSpan.Zero),
        Security = true,
        Severity = UpdateSeverity.High,
        RequiresMigration = true,
        MinSupportedVersion = "0.30.0",
        Summary = "Fixes a session-fixation issue in sign-in.",
    };

    [Fact]
    public void A_manifest_signed_with_the_seed_round_trips_to_all_fields()
    {
        // NewEntry gives the base64 public key + base64 private seed — the same shapes the ceremony prints.
        var key = Ed25519TestKeys.NewEntry("uk-1");
        var manifest = SampleManifest();

        var token = UpdateManifestCodec.Sign(manifest, key.PrivateKeyProtected!);
        var ok = UpdateManifestCodec.TryVerify(token, key.PublicKey, out var verified);

        ok.Should().BeTrue();
        verified.Should().NotBeNull();
        verified!.LatestVersion.Should().Be("0.41.0");
        verified.ReleasedAtUtc.Should().Be(manifest.ReleasedAtUtc);
        verified.Security.Should().BeTrue();
        verified.Severity.Should().Be(UpdateSeverity.High);
        verified.RequiresMigration.Should().BeTrue();
        verified.MinSupportedVersion.Should().Be("0.30.0");
        verified.Summary.Should().Be("Fixes a session-fixation issue in sign-in.");
    }

    [Fact]
    public void A_manifest_signed_by_a_foreign_key_is_rejected()
    {
        var signer = Ed25519TestKeys.NewEntry("uk-signer");
        var other = Ed25519TestKeys.NewEntry("uk-other");

        var token = UpdateManifestCodec.Sign(SampleManifest(), signer.PrivateKeyProtected!);
        var ok = UpdateManifestCodec.TryVerify(token, other.PublicKey, out var verified);

        ok.Should().BeFalse();
        verified.Should().BeNull();
    }

    [Fact]
    public void An_invalid_seed_is_rejected_rather_than_silently_mis_signing()
    {
        var act = () => UpdateManifestCodec.Sign(SampleManifest(), "not-valid-base64!!");
        act.Should().Throw<Exception>();
    }

    [Fact]
    public void The_ceremony_key_pair_actually_signs_and_verifies()
    {
        var block = UpdateKeyCeremony.GenerateConfigBlock(new DateTimeOffset(2026, 7, 11, 0, 0, 0, TimeSpan.Zero));

        var publicKey = ExtractValue(block, "Updates__PublicKey=");
        var seed = ExtractValue(block, "UPDATE_SIGNING_SEED=");

        publicKey.Should().NotBeNullOrWhiteSpace();
        seed.Should().NotBeNullOrWhiteSpace();

        var token = UpdateManifestCodec.Sign(SampleManifest(), seed);
        UpdateManifestCodec.TryVerify(token, publicKey, out var verified).Should().BeTrue();
        verified!.LatestVersion.Should().Be("0.41.0");
    }

    // The ceremony prints ready-to-paste `Key=value` lines; pull the value for a given label back out.
    private static string ExtractValue(string block, string label)
    {
        foreach (var raw in block.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith(label, StringComparison.Ordinal))
            {
                return line[label.Length..].Trim();
            }
        }

        return string.Empty;
    }
}
