using System.Security.Cryptography;
using Microsoft.AspNetCore.Cryptography.KeyDerivation;
using NSec.Cryptography;
using WireHQ.Application.Abstractions.Security;

namespace WireHQ.Identity.Passwords;

/// <summary>
/// Argon2id password hasher (memory-hard, GPU/ASIC-resistant) via libsodium (NSec), with a
/// self-describing, versioned format (<c>ARGON2ID$memory$passes$parallelism$salt$hash</c>). The work
/// factor lives in the hash, so verifying against weaker parameters returns
/// <see cref="PasswordVerificationResult.SuccessRehashNeeded"/> and the caller transparently upgrades it.
///
/// Legacy PBKDF2 hashes (<c>PBKDF2-SHA256$iterations$salt$hash</c>) are still verified, and always
/// signal a re-hash, so existing users are migrated to Argon2id on their next successful login — no
/// forced resets, no data migration. (docs/04-security.md)
/// </summary>
public sealed class PasswordHasher : IPasswordHasher
{
    private const string Argon2Algorithm = "ARGON2ID";
    private const string Pbkdf2Algorithm = "PBKDF2-SHA256";
    private const int SaltSize = 16; // libsodium Argon2id salt size (also used for the legacy PBKDF2 path)
    private const int HashSize = 32;

    // OWASP-recommended Argon2id parameters: m = 19 MiB, t = 2, p = 1. libsodium fixes parallelism at 1.
    // NSec's Argon2Parameters.MemorySize is in KIBIBYTES (not bytes), so 19 MiB = 19456 KiB. The parameters
    // are stored in each hash, so raising them later upgrades existing users on next login.
    private const int CurrentMemoryKib = 19 * 1024; // 19 MiB, expressed in KiB (19456) as NSec requires
    private const int CurrentPasses = 2;
    private const int CurrentParallelism = 1;

    public string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var subkey = DeriveArgon2id(password, salt, CurrentMemoryKib, CurrentPasses, CurrentParallelism, HashSize);

        return string.Join('$',
            Argon2Algorithm, CurrentMemoryKib, CurrentPasses, CurrentParallelism,
            Convert.ToBase64String(salt), Convert.ToBase64String(subkey));
    }

    public PasswordVerificationResult Verify(string password, string hash)
    {
        var parts = hash.Split('$');

        return parts.Length == 0
            ? PasswordVerificationResult.Failed
            : parts[0] switch
            {
                Argon2Algorithm => VerifyArgon2id(password, parts),
                Pbkdf2Algorithm => VerifyPbkdf2(password, parts),
                _ => PasswordVerificationResult.Failed,
            };
    }

    private static PasswordVerificationResult VerifyArgon2id(string password, string[] parts)
    {
        // ARGON2ID$memoryKib$passes$parallelism$salt$hash
        if (parts.Length != 6
            || !int.TryParse(parts[1], out var memoryKib)
            || !int.TryParse(parts[2], out var passes)
            || !int.TryParse(parts[3], out var parallelism))
        {
            return PasswordVerificationResult.Failed;
        }

        if (!TryDecode(parts[4], parts[5], out var salt, out var expected))
        {
            return PasswordVerificationResult.Failed;
        }

        byte[] actual;
        try
        {
            actual = DeriveArgon2id(password, salt, memoryKib, passes, parallelism, expected.Length);
        }
        catch (Exception) // unsupported/out-of-range stored parameters → treat as a non-match
        {
            return PasswordVerificationResult.Failed;
        }

        if (!CryptographicOperations.FixedTimeEquals(actual, expected))
        {
            return PasswordVerificationResult.Failed;
        }

        // Upgrade if the stored cost is below what we issue today.
        return memoryKib < CurrentMemoryKib || passes < CurrentPasses
            ? PasswordVerificationResult.SuccessRehashNeeded
            : PasswordVerificationResult.Success;
    }

    private static PasswordVerificationResult VerifyPbkdf2(string password, string[] parts)
    {
        // Legacy PBKDF2-SHA256$iterations$salt$hash. Valid hashes always signal a re-hash so the next
        // successful login migrates the user to Argon2id.
        if (parts.Length != 4 || !int.TryParse(parts[1], out var iterations))
        {
            return PasswordVerificationResult.Failed;
        }

        if (!TryDecode(parts[2], parts[3], out var salt, out var expected))
        {
            return PasswordVerificationResult.Failed;
        }

        var actual = KeyDerivation.Pbkdf2(password, salt, KeyDerivationPrf.HMACSHA256, iterations, expected.Length);

        return CryptographicOperations.FixedTimeEquals(actual, expected)
            ? PasswordVerificationResult.SuccessRehashNeeded
            : PasswordVerificationResult.Failed;
    }

    private static byte[] DeriveArgon2id(string password, byte[] salt, int memoryKib, int passes, int parallelism, int size)
    {
        var parameters = new Argon2Parameters
        {
            DegreeOfParallelism = parallelism,
            MemorySize = memoryKib, // NSec expects kibibytes
            NumberOfPasses = passes,
        };

        return PasswordBasedKeyDerivationAlgorithm.Argon2id(in parameters).DeriveBytes(password, salt, size);
    }

    private static bool TryDecode(string saltB64, string hashB64, out byte[] salt, out byte[] hash)
    {
        salt = [];
        hash = [];
        try
        {
            salt = Convert.FromBase64String(saltB64);
            hash = Convert.FromBase64String(hashB64);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
