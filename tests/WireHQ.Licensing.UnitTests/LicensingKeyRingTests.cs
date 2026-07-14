using FluentAssertions;
using WireHQ.Application.Abstractions.Security;
using Xunit;

namespace WireHQ.Licensing.UnitTests;

/// <summary>
/// The key ring's construction and validation: it fails fast and clearly on misconfiguration (a missing
/// or dangling active key, duplicate ids, bad key material), supports a verify-only deployment (public
/// keys with no private key), and decrypts the private key through <see cref="ISecretProtector"/>.
/// </summary>
public sealed class LicensingKeyRingTests
{
    private static readonly ISecretProtector Protector = new PassthroughSecretProtector();

    [Fact]
    public void A_signing_ring_can_sign_and_expose_its_active_key_id()
    {
        using var ring = Ed25519TestKeys.SigningRing("active-1");
        ring.ActiveKeyId.Should().Be("active-1");
        ring.CanSign.Should().BeTrue();
        ring.Invoking(r => r.ActiveSigningKey).Should().NotThrow();
        ring.TryGetPublicKey("active-1", out _).Should().BeTrue();
        ring.TryGetPublicKey("nope", out _).Should().BeFalse();
    }

    [Fact]
    public void A_verify_only_ring_has_public_keys_but_cannot_sign()
    {
        var entry = Ed25519TestKeys.NewEntry("verify-only");
        entry.PrivateKeyProtected = null; // public key alone (the future self-hosted posture)

        using var ring = LicensingKeyRing.Create(
            new LicensingKeyOptions { ActiveKeyId = "verify-only", Keys = [entry] }, Protector);

        ring.CanSign.Should().BeFalse();
        ring.TryGetPublicKey("verify-only", out _).Should().BeTrue();
        ring.Invoking(r => r.ActiveSigningKey).Should().Throw<InvalidOperationException>()
            .WithMessage("*verify but not sign*");
    }

    [Fact]
    public void Missing_active_key_id_is_rejected()
    {
        var act = () => LicensingKeyRing.Create(
            new LicensingKeyOptions { ActiveKeyId = "", Keys = [Ed25519TestKeys.NewEntry("k")] }, Protector);
        act.Should().Throw<InvalidOperationException>().WithMessage("*ActiveKeyId*");
    }

    [Fact]
    public void An_empty_key_list_is_rejected()
    {
        var act = () => LicensingKeyRing.Create(
            new LicensingKeyOptions { ActiveKeyId = "k", Keys = [] }, Protector);
        act.Should().Throw<InvalidOperationException>().WithMessage("*at least one key*");
    }

    [Fact]
    public void An_active_key_id_not_present_among_keys_is_rejected()
    {
        var act = () => LicensingKeyRing.Create(
            new LicensingKeyOptions { ActiveKeyId = "ghost", Keys = [Ed25519TestKeys.NewEntry("real")] }, Protector);
        act.Should().Throw<InvalidOperationException>().WithMessage("*not among*");
    }

    [Fact]
    public void Duplicate_key_ids_are_rejected()
    {
        var act = () => LicensingKeyRing.Create(
            new LicensingKeyOptions { ActiveKeyId = "dup", Keys = [Ed25519TestKeys.NewEntry("dup"), Ed25519TestKeys.NewEntry("dup")] },
            Protector);
        act.Should().Throw<InvalidOperationException>().WithMessage("*Duplicate*");
    }

    [Fact]
    public void A_non_base64_public_key_is_rejected()
    {
        var entry = Ed25519TestKeys.NewEntry("bad");
        entry.PublicKey = "not base64!";
        var act = () => LicensingKeyRing.Create(
            new LicensingKeyOptions { ActiveKeyId = "bad", Keys = [entry] }, Protector);
        act.Should().Throw<InvalidOperationException>().WithMessage("*not valid base64*");
    }

    [Fact]
    public void A_public_key_of_the_wrong_length_is_rejected()
    {
        var entry = Ed25519TestKeys.NewEntry("short");
        entry.PublicKey = Convert.ToBase64String(new byte[16]); // not 32 bytes
        var act = () => LicensingKeyRing.Create(
            new LicensingKeyOptions { ActiveKeyId = "short", Keys = [entry] }, Protector);
        act.Should().Throw<InvalidOperationException>().WithMessage("*Ed25519 public key*");
    }

    [Fact]
    public void A_private_key_that_fails_to_decrypt_is_reported_clearly()
    {
        var entry = Ed25519TestKeys.NewEntry("enc");
        var act = () => LicensingKeyRing.Create(
            new LicensingKeyOptions { ActiveKeyId = "enc", Keys = [entry] }, new ThrowingSecretProtector());
        act.Should().Throw<InvalidOperationException>().WithMessage("*could not be decrypted*");
    }

    private sealed class ThrowingSecretProtector : ISecretProtector
    {
        public string Protect(string plaintext) => plaintext;

        public string Unprotect(string ciphertext) => throw new InvalidOperationException("bad key");
    }
}
