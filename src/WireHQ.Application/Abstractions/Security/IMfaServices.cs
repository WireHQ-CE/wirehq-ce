namespace WireHQ.Application.Abstractions.Security;

/// <summary>TOTP (RFC 6238) enrolment and verification. Implemented in WireHQ.Identity.</summary>
public interface ITotpService
{
    /// <summary>Create a new shared secret + provisioning URI/QR for authenticator-app enrolment.</summary>
    TotpEnrolment CreateEnrolment(string accountName, string issuer = "WireHQ");

    /// <summary>Validate a 6-digit code against a secret, tolerating small clock skew.</summary>
    bool VerifyCode(string secret, string code);
}

public sealed record TotpEnrolment(string Secret, string OtpAuthUri, string QrCodePngBase64);

/// <summary>Generates and verifies single-use MFA recovery codes.</summary>
public interface IRecoveryCodeService
{
    /// <summary>Generate N human-readable codes (returned once) plus their hashes to store.</summary>
    IReadOnlyList<RecoveryCodePair> Generate(int count = 10);

    bool Verify(string code, string codeHash);
}

public sealed record RecoveryCodePair(string Code, string CodeHash);

/// <summary>
/// Envelope encryption for sensitive columns (TOTP secrets, provider tokens). Abstracts the
/// KMS: self-hosted can use a local key, SaaS a managed KMS. (docs/04-security.md)
/// </summary>
public interface ISecretProtector
{
    string Protect(string plaintext);

    string Unprotect(string ciphertext);
}
