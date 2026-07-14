using System.Text;
using FluentAssertions;
using NSec.Cryptography;
using WireHQ.Licensing.Paseto;
using Xunit;

namespace WireHQ.Licensing.UnitTests;

/// <summary>
/// Behavioural tests for the <see cref="PasetoV4Public"/> primitive beyond the spec vectors: the
/// round-trip, and — the point of a signed token — that every kind of tampering or wrong input is
/// rejected rather than silently accepted or thrown on.
/// </summary>
public sealed class PasetoV4PublicTests
{
    private static (Key Key, PublicKey PublicKey) NewKeyPair()
    {
        var key = Key.Create(
            SignatureAlgorithm.Ed25519,
            new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });
        return (key, key.PublicKey);
    }

    private static byte[] Utf8(string value) => Encoding.UTF8.GetBytes(value);

    [Fact]
    public void Sign_then_verify_round_trips_the_message()
    {
        var (key, publicKey) = NewKeyPair();
        using (key)
        {
            var message = Utf8("{\"lid\":\"01AB\",\"mod\":\"remote-deployment\"}");

            var token = PasetoV4Public.Sign(message, ReadOnlySpan<byte>.Empty, key);
            token.Should().StartWith("v4.public.");

            PasetoV4Public.TryVerify(token, publicKey, out var verified).Should().BeTrue();
            verified.Should().Equal(message);
        }
    }

    [Fact]
    public void Signing_is_deterministic_for_the_same_message_and_key()
    {
        var (key, _) = NewKeyPair();
        using (key)
        {
            var message = Utf8("stable");
            var a = PasetoV4Public.Sign(message, ReadOnlySpan<byte>.Empty, key);
            var b = PasetoV4Public.Sign(message, ReadOnlySpan<byte>.Empty, key);
            a.Should().Be(b, because: "Ed25519 is deterministic — the same input yields the same token");
        }
    }

    [Fact]
    public void Footer_can_be_read_before_verification_and_round_trips()
    {
        var (key, publicKey) = NewKeyPair();
        using (key)
        {
            var footer = Utf8("{\"kid\":\"k7\"}");
            var token = PasetoV4Public.Sign(Utf8("m"), footer, key);

            PasetoV4Public.TryReadFooter(token, out var readFooter).Should().BeTrue();
            readFooter.Should().Equal(footer);
            PasetoV4Public.TryVerify(token, publicKey, out _).Should().BeTrue();
        }
    }

    [Fact]
    public void A_token_with_no_footer_reads_an_empty_footer()
    {
        var (key, _) = NewKeyPair();
        using (key)
        {
            var token = PasetoV4Public.Sign(Utf8("m"), ReadOnlySpan<byte>.Empty, key);
            PasetoV4Public.TryReadFooter(token, out var footer).Should().BeTrue();
            footer.Should().BeEmpty();
        }
    }

    [Fact]
    public void A_wrong_public_key_does_not_verify()
    {
        var (key, _) = NewKeyPair();
        var (_, otherPublicKey) = NewKeyPair();
        using (key)
        {
            var token = PasetoV4Public.Sign(Utf8("m"), ReadOnlySpan<byte>.Empty, key);
            PasetoV4Public.TryVerify(token, otherPublicKey, out _).Should().BeFalse();
        }
    }

    [Fact]
    public void A_tampered_payload_does_not_verify()
    {
        var (key, publicKey) = NewKeyPair();
        using (key)
        {
            var token = PasetoV4Public.Sign(Utf8("original"), ReadOnlySpan<byte>.Empty, key);

            // Flip one character in the base64url body.
            var body = token["v4.public.".Length..];
            var flippedChar = body[0] == 'A' ? 'B' : 'A';
            var tampered = "v4.public." + flippedChar + body[1..];

            PasetoV4Public.TryVerify(tampered, publicKey, out _).Should().BeFalse();
        }
    }

    [Fact]
    public void A_tampered_footer_does_not_verify()
    {
        var (key, publicKey) = NewKeyPair();
        using (key)
        {
            var token = PasetoV4Public.Sign(Utf8("m"), Utf8("{\"kid\":\"a\"}"), key);
            var swappedFooter = token[..(token.LastIndexOf('.') + 1)]
                + System.Buffers.Text.Base64Url.EncodeToString(Utf8("{\"kid\":\"b\"}"));

            PasetoV4Public.TryVerify(swappedFooter, publicKey, out _)
                .Should().BeFalse(because: "the footer is authenticated by the PAE");
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-token")]
    [InlineData("v3.public.abc")]                 // wrong version
    [InlineData("v4.local.abc")]                  // wrong purpose
    [InlineData("v4.public.")]                    // empty payload
    [InlineData("v4.public.@@@not-base64@@@")]    // undecodable body
    [InlineData("v4.public.aGVsbG8.ftr.extra")]   // too many segments
    public void Malformed_tokens_are_rejected_without_throwing(string token)
    {
        var (_, publicKey) = NewKeyPair();
        PasetoV4Public.TryVerify(token, publicKey, out _).Should().BeFalse();
    }

    [Fact]
    public void A_body_shorter_than_a_signature_is_rejected()
    {
        var (_, publicKey) = NewKeyPair();
        var tooShort = "v4.public." + System.Buffers.Text.Base64Url.EncodeToString(new byte[10]);
        PasetoV4Public.TryVerify(tooShort, publicKey, out _).Should().BeFalse();
    }
}
