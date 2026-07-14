namespace WireHQ.Application.Abstractions.Security;

/// <summary>
/// One-way password hashing. The stored hash is self-describing (algorithm + parameters), so
/// the verifier can transparently re-hash on login when parameters are hardened or the
/// algorithm is upgraded (PBKDF2 → Argon2id) without a migration. (docs/04-security.md)
/// </summary>
public interface IPasswordHasher
{
    string Hash(string password);

    PasswordVerificationResult Verify(string password, string hash);
}

public enum PasswordVerificationResult
{
    Failed = 0,
    Success = 1,
    /// <summary>Valid, but stored with outdated parameters — caller should re-hash and persist.</summary>
    SuccessRehashNeeded = 2,
}
