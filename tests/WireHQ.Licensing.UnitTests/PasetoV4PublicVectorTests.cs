using System.Text;
using FluentAssertions;
using NSec.Cryptography;
using WireHQ.Licensing.Paseto;
using Xunit;

namespace WireHQ.Licensing.UnitTests;

/// <summary>
/// Known-answer tests against the <b>official</b> PASETO v4.public test vectors
/// (paseto-standard/test-vectors, <c>v4.json</c>, the <c>4-S-*</c> cases). Because Ed25519 is
/// deterministic, a correct implementation reproduces each published token byte-for-byte — so these
/// pin our construction (PAE, base64url, header, footer, implicit assertion) to the spec. If any of it
/// drifts, these fail. This is the proof that <see cref="PasetoV4Public"/> is the standard, not a
/// look-alike.
/// </summary>
public sealed class PasetoV4PublicVectorTests
{
    // The vectors' shared Ed25519 test key pair (public test material from the PASETO spec, not a secret).
    private const string SecretKeyHex = // gitleaks:allow
        "b4cbfb43df4ce210727d953e4a713307fa19bb7d9f85041438d9e11b942a37741eb9dbbbbc047c03fd70604e0071f0987e16b28b757225c11f00415d0e20b1a2";
    private const string PublicKeyHex =
        "1eb9dbbbbc047c03fd70604e0071f0987e16b28b757225c11f00415d0e20b1a2";

    private const string Payload =
        "{\"data\":\"this is a signed message\",\"exp\":\"2022-01-01T00:00:00+00:00\"}";
    private const string Footer =
        "{\"kid\":\"zVhMiPBP9fRf2snEcT7gFTioeA9COcNy9DfgL1W60haN\"}";
    private const string ImplicitAssertion = "{\"test-vector\":\"4-S-3\"}";

    private const string Token41 =
        "v4.public.eyJkYXRhIjoidGhpcyBpcyBhIHNpZ25lZCBtZXNzYWdlIiwiZXhwIjoiMjAyMi0wMS0wMVQwMDowMDowMCswMDowMCJ9bg_XBBzds8lTZShVlwwKSgeKpLT3yukTw6JUz3W4h_ExsQV-P0V54zemZDcAxFaSeef1QlXEFtkqxT1ciiQEDA";
    private const string Token42 =
        "v4.public.eyJkYXRhIjoidGhpcyBpcyBhIHNpZ25lZCBtZXNzYWdlIiwiZXhwIjoiMjAyMi0wMS0wMVQwMDowMDowMCswMDowMCJ9v3Jt8mx_TdM2ceTGoqwrh4yDFn0XsHvvV_D0DtwQxVrJEBMl0F2caAdgnpKlt4p7xBnx1HcO-SPo8FPp214HDw.eyJraWQiOiJ6VmhNaVBCUDlmUmYyc25FY1Q3Z0ZUaW9lQTlDT2NOeTlEZmdMMVc2MGhhTiJ9";
    private const string Token43 =
        "v4.public.eyJkYXRhIjoidGhpcyBpcyBhIHNpZ25lZCBtZXNzYWdlIiwiZXhwIjoiMjAyMi0wMS0wMVQwMDowMDowMCswMDowMCJ9NPWciuD3d0o5eXJXG5pJy-DiVEoyPYWs1YSTwWHNJq6DZD3je5gf-0M4JR9ipdUSJbIovzmBECeaWmaqcaP0DQ.eyJraWQiOiJ6VmhNaVBCUDlmUmYyc25FY1Q3Z0ZUaW9lQTlDT2NOeTlEZmdMMVc2MGhhTiJ9";

    [Fact]
    public void Vector_4_S_1_no_footer_signs_to_the_exact_token()
    {
        using var key = ImportSecretKey();
        var token = PasetoV4Public.Sign(Utf8(Payload), ReadOnlySpan<byte>.Empty, key);
        token.Should().Be(Token41);
    }

    [Fact]
    public void Vector_4_S_2_with_footer_signs_to_the_exact_token()
    {
        using var key = ImportSecretKey();
        var token = PasetoV4Public.Sign(Utf8(Payload), Utf8(Footer), key);
        token.Should().Be(Token42);
    }

    [Fact]
    public void Vector_4_S_3_with_footer_and_implicit_assertion_signs_to_the_exact_token()
    {
        using var key = ImportSecretKey();
        var token = PasetoV4Public.Sign(Utf8(Payload), Utf8(Footer), key, Utf8(ImplicitAssertion));
        token.Should().Be(Token43);
    }

    [Theory]
    [InlineData(Token41)]
    [InlineData(Token42)]
    public void Vectors_verify_and_return_the_payload(string token)
    {
        var publicKey = ImportPublicKey();
        PasetoV4Public.TryVerify(token, publicKey, out var message).Should().BeTrue();
        Encoding.UTF8.GetString(message).Should().Be(Payload);
    }

    [Fact]
    public void Vector_4_S_3_verifies_only_with_the_matching_implicit_assertion()
    {
        var publicKey = ImportPublicKey();

        PasetoV4Public.TryVerify(Token43, publicKey, out var message, Utf8(ImplicitAssertion)).Should().BeTrue();
        Encoding.UTF8.GetString(message).Should().Be(Payload);

        // The implicit assertion is authenticated: verifying without it (or with a different one) fails.
        PasetoV4Public.TryVerify(Token43, publicKey, out _).Should().BeFalse();
        PasetoV4Public.TryVerify(Token43, publicKey, out _, Utf8("{\"test-vector\":\"4-S-2\"}")).Should().BeFalse();
    }

    private static Key ImportSecretKey()
    {
        // The 64-byte Ed25519 secret key is seed(32) ‖ public(32); NSec's raw private key is the seed.
        var secretKey = Convert.FromHexString(SecretKeyHex);
        return Key.Import(SignatureAlgorithm.Ed25519, secretKey.AsSpan(0, 32), KeyBlobFormat.RawPrivateKey);
    }

    private static PublicKey ImportPublicKey() =>
        PublicKey.Import(SignatureAlgorithm.Ed25519, Convert.FromHexString(PublicKeyHex), KeyBlobFormat.RawPublicKey);

    private static byte[] Utf8(string value) => Encoding.UTF8.GetBytes(value);
}
