using System.Security.Cryptography;
using FluentAssertions;
using Microsoft.AspNetCore.Cryptography.KeyDerivation;
using WireHQ.Application.Abstractions.Security;
using WireHQ.Identity.Passwords;
using Xunit;

namespace WireHQ.Api.IntegrationTests;

/// <summary>
/// Unit coverage for the Argon2id <see cref="PasswordHasher"/> and its transparent migration of legacy
/// PBKDF2 hashes. Pure in-memory — no web host or database.
/// </summary>
public sealed class PasswordHasherTests
{
    private readonly PasswordHasher _hasher = new();

    [Fact]
    public void Hash_emits_the_argon2id_format()
    {
        var hash = _hasher.Hash("correct horse battery staple");

        hash.Should().StartWith("ARGON2ID$");
        // ARGON2ID$memory$passes$parallelism$salt$hash — six parts.
        hash.Split('$').Should().HaveCount(6);
    }

    [Fact]
    public void Hash_then_verify_succeeds_and_needs_no_rehash()
    {
        var hash = _hasher.Hash("s3cret-pass");

        _hasher.Verify("s3cret-pass", hash).Should().Be(PasswordVerificationResult.Success);
    }

    [Fact]
    public void Verify_rejects_a_wrong_password()
    {
        var hash = _hasher.Hash("s3cret-pass");

        _hasher.Verify("not-the-password", hash).Should().Be(PasswordVerificationResult.Failed);
    }

    [Fact]
    public void Salt_is_random_so_two_hashes_of_the_same_password_differ()
    {
        _hasher.Hash("same").Should().NotBe(_hasher.Hash("same"));
    }

    [Fact]
    public void Legacy_pbkdf2_hash_verifies_and_signals_a_rehash_to_argon2id()
    {
        var legacy = MakePbkdf2Hash("legacy-user-pw", iterations: 600_000);

        _hasher.Verify("legacy-user-pw", legacy).Should().Be(PasswordVerificationResult.SuccessRehashNeeded);
        _hasher.Verify("wrong", legacy).Should().Be(PasswordVerificationResult.Failed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-hash")]
    [InlineData("ARGON2ID$notanumber$2$1$AAAA$AAAA")]
    [InlineData("ARGON2ID$19456$2$1$not-base64!$also-bad!")]
    public void Malformed_hashes_fail_cleanly(string hash)
    {
        _hasher.Verify("whatever", hash).Should().Be(PasswordVerificationResult.Failed);
    }

    private static string MakePbkdf2Hash(string password, int iterations)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var subkey = KeyDerivation.Pbkdf2(password, salt, KeyDerivationPrf.HMACSHA256, iterations, 32);
        return string.Join('$', "PBKDF2-SHA256", iterations, Convert.ToBase64String(salt), Convert.ToBase64String(subkey));
    }
}
