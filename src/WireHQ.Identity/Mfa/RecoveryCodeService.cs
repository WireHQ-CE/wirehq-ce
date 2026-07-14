using System.Security.Cryptography;
using WireHQ.Application.Abstractions.Security;

namespace WireHQ.Identity.Mfa;

/// <summary>
/// Generates single-use MFA recovery codes (shown once) and their hashes for storage. Codes are
/// hashed with the same hasher as passwords, so a leaked database does not expose usable codes.
/// </summary>
public sealed class RecoveryCodeService(IPasswordHasher passwordHasher) : IRecoveryCodeService
{
    // Unambiguous alphabet (no 0/O/1/I) for readable codes.
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    public IReadOnlyList<RecoveryCodePair> Generate(int count = 10)
    {
        var pairs = new List<RecoveryCodePair>(count);
        for (var i = 0; i < count; i++)
        {
            var code = $"{RandomBlock(4)}-{RandomBlock(4)}";
            pairs.Add(new RecoveryCodePair(code, passwordHasher.Hash(code)));
        }

        return pairs;
    }

    public bool Verify(string code, string codeHash) =>
        passwordHasher.Verify(code.Trim().ToUpperInvariant(), codeHash) != PasswordVerificationResult.Failed;

    private static string RandomBlock(int length)
    {
        Span<char> chars = stackalloc char[length];
        for (var i = 0; i < length; i++)
        {
            chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        }

        return new string(chars);
    }
}
