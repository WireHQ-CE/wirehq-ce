using FluentAssertions;
using WireHQ.Application.Abstractions.Licensing;
using Xunit;

namespace WireHQ.Licensing.UnitTests;

/// <summary>
/// The signer + verifier working together over the real claim contracts — the behaviour the licensing
/// service and (later) the Community Edition depend on: an honest token round-trips to its claims; a
/// wrong key id, a tampered token, or an expired/not-yet-valid token is rejected with a precise reason;
/// and key rotation keeps older tokens verifiable.
/// </summary>
public sealed class LicenceTokenCodecTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 5, 12, 0, 0, TimeSpan.Zero);

    private static LicenceTokenVerifier VerifierOver(LicensingKeyRing ring, DateTimeOffset? now = null) =>
        new(ring, new FixedClock(now ?? Now));

    [Fact]
    public void A_signed_licence_key_round_trips_to_its_claims()
    {
        using var ring = Ed25519TestKeys.SigningRing("lk-1");
        var signer = new LicenceTokenSigner(ring);
        var verifier = VerifierOver(ring);

        var claims = new LicenceKeyClaims
        {
            LicenceId = "0199-lid",
            ModuleSlug = "remote-deployment",
            BuyerEmailHash = "sha256:abc",
            IssuedAtUtc = Now,
            UpdateWindowEndUtc = Now.AddYears(1),
        };

        var token = signer.Sign(claims);
        var result = verifier.Verify<LicenceKeyClaims>(token);

        result.IsValid.Should().BeTrue();
        result.KeyId.Should().Be("lk-1");
        result.Claims.Should().Be(claims, because: "a licence key has no exp and must survive the round-trip verbatim");
    }

    [Fact]
    public void An_activation_token_within_grace_is_valid_and_past_grace_is_expired()
    {
        using var ring = Ed25519TestKeys.SigningRing("at-1");
        var signer = new LicenceTokenSigner(ring);

        var claims = new ActivationTokenClaims
        {
            LicenceId = "0199-lid",
            InstanceFingerprint = "fp-xyz",
            IssuedAtUtc = Now,
            NextVerifyByUtc = Now.AddDays(7),
            GraceEndsUtc = Now.AddDays(30),
        };
        var token = signer.Sign(claims);

        // Inside the grace window → valid.
        VerifierOver(ring, Now.AddDays(10)).Verify<ActivationTokenClaims>(token)
            .IsValid.Should().BeTrue();

        // Past the grace window (exp) → the verifier itself rejects it.
        var expired = VerifierOver(ring, Now.AddDays(31)).Verify<ActivationTokenClaims>(token);
        expired.IsValid.Should().BeFalse();
        expired.Failure.Should().Be(LicenceTokenFailure.Expired);
    }

    [Fact]
    public void A_not_yet_valid_token_is_rejected()
    {
        using var ring = Ed25519TestKeys.SigningRing("nbf-1");
        var signer = new LicenceTokenSigner(ring);

        // A payload carrying an nbf in the future (a hypothetical future-dated activation).
        var claims = new TimeBoundClaims { NotBeforeUtc = Now.AddDays(5) };
        var token = signer.Sign(claims);

        var result = VerifierOver(ring).Verify<TimeBoundClaims>(token);
        result.IsValid.Should().BeFalse();
        result.Failure.Should().Be(LicenceTokenFailure.NotYetValid);
    }

    [Fact]
    public void A_token_signed_by_an_unknown_key_is_rejected_as_unknown_key_id()
    {
        using var signerRing = Ed25519TestKeys.SigningRing("signer-kid");
        using var verifierRing = Ed25519TestKeys.SigningRing("verifier-kid"); // different key entirely
        var token = new LicenceTokenSigner(signerRing).Sign(new LicenceKeyClaims
        {
            LicenceId = "l", ModuleSlug = "m", BuyerEmailHash = "h", IssuedAtUtc = Now, UpdateWindowEndUtc = Now.AddYears(1),
        });

        var result = VerifierOver(verifierRing).Verify<LicenceKeyClaims>(token);
        result.IsValid.Should().BeFalse();
        result.Failure.Should().Be(LicenceTokenFailure.UnknownKeyId);
        result.KeyId.Should().Be("signer-kid", because: "the footer's kid is still surfaced for diagnostics");
    }

    [Fact]
    public void A_tampered_token_is_rejected_as_a_bad_signature()
    {
        using var ring = Ed25519TestKeys.SigningRing("t-1");
        var token = new LicenceTokenSigner(ring).Sign(new LicenceKeyClaims
        {
            LicenceId = "l", ModuleSlug = "m", BuyerEmailHash = "h", IssuedAtUtc = Now, UpdateWindowEndUtc = Now.AddYears(1),
        });

        var body = token["v4.public.".Length..];
        var flipped = body[0] == 'A' ? 'B' : 'A';
        var tampered = "v4.public." + flipped + body[1..];

        var result = VerifierOver(ring).Verify<LicenceKeyClaims>(tampered);
        result.IsValid.Should().BeFalse();
        result.Failure.Should().Be(LicenceTokenFailure.BadSignature);
    }

    [Fact]
    public void Garbage_input_is_rejected_as_malformed()
    {
        using var ring = Ed25519TestKeys.SigningRing();
        var verifier = VerifierOver(ring);

        verifier.Verify<LicenceKeyClaims>("").Failure.Should().Be(LicenceTokenFailure.Malformed);
        verifier.Verify<LicenceKeyClaims>("nonsense").Failure.Should().Be(LicenceTokenFailure.Malformed);
    }

    [Fact]
    public void After_rotation_a_token_signed_by_the_previous_key_still_verifies()
    {
        // Two keys: the old signing key and a new active key. The rotated ring keeps the old public key.
        var oldEntry = Ed25519TestKeys.NewEntry("kid-old");
        var newEntry = Ed25519TestKeys.NewEntry("kid-new");
        var protector = new PassthroughSecretProtector();

        using var oldSigningRing = LicensingKeyRing.Create(
            new LicensingKeyOptions { ActiveKeyId = "kid-old", Keys = [oldEntry] }, protector);
        using var rotatedRing = LicensingKeyRing.Create(
            new LicensingKeyOptions { ActiveKeyId = "kid-new", Keys = [newEntry, oldEntry] }, protector);

        var legacyToken = new LicenceTokenSigner(oldSigningRing).Sign(new LicenceKeyClaims
        {
            LicenceId = "l", ModuleSlug = "m", BuyerEmailHash = "h", IssuedAtUtc = Now, UpdateWindowEndUtc = Now.AddYears(1),
        });

        // The rotated deployment signs new tokens under kid-new but still verifies the kid-old token.
        new LicenceTokenSigner(rotatedRing).ActiveKeyId.Should().Be("kid-new");
        var result = VerifierOver(rotatedRing).Verify<LicenceKeyClaims>(legacyToken);
        result.IsValid.Should().BeTrue();
        result.KeyId.Should().Be("kid-old");
    }

    /// <summary>A minimal claims shape carrying only an <c>nbf</c>, to exercise not-yet-valid handling.</summary>
    private sealed record TimeBoundClaims
    {
        [System.Text.Json.Serialization.JsonPropertyName("nbf")]
        public required DateTimeOffset NotBeforeUtc { get; init; }
    }
}
