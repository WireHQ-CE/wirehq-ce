using System.Security.Cryptography;
using System.Text;
using WireHQ.Domain.Common;
using WireHQ.Shared.Results;

namespace WireHQ.Domain.Identity;

/// <summary>
/// A single-use, short-lived <b>phone-ownership</b> challenge (docs/36-notifications.md §5/§8, SMS Wave 4). Proves the
/// user controls the number before it can receive alerts — the ownership half of the compliance spine (consent is a
/// separate, immutable audited fact; a verified number is NOT consent to receive — docs/36 §4).
/// <para>Hardened like the email-verification token, plus OTP-specific defences the SMS review demanded (docs/36 §5):
/// the 6-digit code is stored <b>only as a salted hash</b> (never plaintext); <see cref="Confirm"/> compares with
/// <see cref="CryptographicOperations.FixedTimeEquals"/>; at most <see cref="MaxAttempts"/> guesses then the challenge
/// is burned; a ~<see cref="ExpiryMinutes"/>-minute expiry; single-use (<see cref="ConsumedAtUtc"/>). Every non-success
/// outcome maps to ONE generic caller error so the endpoint is non-oracular.</para>
/// Tenant-owned in the <c>identity</c> schema (carries <c>organization_id</c> ⇒ RLS-covered automatically); SMS is an
/// org-scoped capability, so a challenge belongs to the (org, user) that requested it.
/// </summary>
public sealed class PhoneVerification : Entity, ITenantOwned
{
    /// <summary>Max guesses before the challenge is invalidated (docs/36 §5).</summary>
    public const int MaxAttempts = 5;

    /// <summary>Single-use validity window (docs/36 §5): ~10 minutes.</summary>
    public const int ExpiryMinutes = 10;

    // EF Core
    private PhoneVerification()
    {
    }

    private PhoneVerification(Guid id, Guid organizationId, Guid userId, string phoneE164, string codeHash, DateTimeOffset createdAtUtc)
        : base(id)
    {
        OrganizationId = organizationId;
        UserId = userId;
        PhoneE164 = phoneE164;
        CodeHash = codeHash;
        CreatedAtUtc = createdAtUtc;
        ExpiresAtUtc = createdAtUtc.AddMinutes(ExpiryMinutes);
    }

    public Guid OrganizationId { get; private set; }

    public Guid UserId { get; private set; }

    /// <summary>The E.164 number being verified. The plaintext number is needed to send the code + set it on the user
    /// on success; it is never surfaced by a query and masked in any log (docs/36 §5).</summary>
    public string PhoneE164 { get; private set; } = null!;

    /// <summary>The salted hash of the OTP (hex). The plaintext code is NEVER stored (docs/36 §5).</summary>
    public string CodeHash { get; private set; } = null!;

    public int Attempts { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset ExpiresAtUtc { get; private set; }

    /// <summary>Set once the challenge is spent — on success, on expiry-burn, or when the attempt cap is hit. A
    /// consumed challenge is single-use and can never verify again.</summary>
    public DateTimeOffset? ConsumedAtUtc { get; private set; }

    /// <summary>True while the challenge can still be confirmed (unspent + unexpired).</summary>
    public bool IsActive(DateTimeOffset nowUtc) => ConsumedAtUtc is null && ExpiresAtUtc > nowUtc;

    /// <summary>Issue a challenge for (org, user, number). The freshly generated <paramref name="code"/> is hashed here
    /// with the new row's id as salt and only the hash is stored — the plaintext code is delivered out-of-band and NEVER
    /// persisted (docs/36 §5). Salting with the id (generated here) keeps the salt unique per challenge.</summary>
    public static PhoneVerification Issue(Guid organizationId, Guid userId, string phoneE164, string code, DateTimeOffset nowUtc)
    {
        var id = Guid.CreateVersion7();
        return new(id, organizationId, userId, phoneE164, HashCode(id, code), nowUtc);
    }

    /// <summary>
    /// Attempt to confirm the challenge with the presented code's salted hash. Constant-time, attempt-capped and
    /// single-use; returns exactly one <see cref="PhoneVerificationOutcome"/> which the handler collapses to a single
    /// generic caller error for every non-<see cref="PhoneVerificationOutcome.Verified"/> value (no oracle — docs/36 §5).
    /// </summary>
    public PhoneVerificationOutcome Confirm(string presentedCodeHash, DateTimeOffset nowUtc)
    {
        if (ConsumedAtUtc is not null)
        {
            return PhoneVerificationOutcome.Invalidated;
        }

        if (nowUtc >= ExpiresAtUtc)
        {
            ConsumedAtUtc = nowUtc; // burn an expired challenge so it can't be probed further
            return PhoneVerificationOutcome.Expired;
        }

        if (Attempts >= MaxAttempts)
        {
            ConsumedAtUtc = nowUtc;
            return PhoneVerificationOutcome.TooManyAttempts;
        }

        Attempts++;

        if (FixedTimeEquals(CodeHash, presentedCodeHash))
        {
            ConsumedAtUtc = nowUtc; // single-use: consume on success
            return PhoneVerificationOutcome.Verified;
        }

        if (Attempts >= MaxAttempts)
        {
            ConsumedAtUtc = nowUtc; // burn after the final wrong guess
        }

        return PhoneVerificationOutcome.Mismatch;
    }

    /// <summary>Invalidate an outstanding challenge (single-use), e.g. when a newer one supersedes it.</summary>
    public void Supersede(DateTimeOffset nowUtc) => ConsumedAtUtc ??= nowUtc;

    /// <summary>
    /// The stored/compared code hash: a SHA-256 hex digest over the per-challenge id (salt) + the code. Salting with
    /// the unique row id defeats a precomputed-table attack on the tiny 6-digit space if the store is ever read
    /// (docs/36 §5) — the id is known only alongside the row, and each challenge has a distinct salt.
    /// </summary>
    public static string HashCode(Guid verificationId, string code)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{verificationId:N}:{code}"));
        return Convert.ToHexString(bytes);
    }

    private static bool FixedTimeEquals(string expectedHex, string presentedHex)
    {
        // Decode defensively — a malformed presented hash must never throw (that would itself be an oracle). Length
        // mismatch ⇒ not equal (FixedTimeEquals requires equal-length spans; both are 32-byte SHA-256 digests).
        byte[] expected, presented;
        try
        {
            expected = Convert.FromHexString(expectedHex);
            presented = Convert.FromHexString(presentedHex);
        }
        catch (FormatException)
        {
            return false;
        }

        return expected.Length == presented.Length && CryptographicOperations.FixedTimeEquals(expected, presented);
    }
}

/// <summary>The single outcome of a <see cref="PhoneVerification.Confirm"/> attempt. Only <see cref="Verified"/> is a
/// success; the handler maps every other value to ONE generic error so the endpoint reveals nothing (docs/36 §5).</summary>
public enum PhoneVerificationOutcome
{
    Verified,
    Mismatch,
    Expired,
    TooManyAttempts,
    Invalidated,
}

public static class PhoneVerificationErrors
{
    /// <summary>The single generic failure surfaced for EVERY non-success confirm outcome — no "wrong/expired/locked"
    /// oracle (docs/36 §5).</summary>
    public static readonly Error Failed = Error.Validation("account.phone_verification_failed", "That code is invalid or has expired. Request a new one.");
}
