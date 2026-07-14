namespace WireHQ.Application.Abstractions.Licensing;

/// <summary>
/// Why a licence token failed verification. Deliberately granular so the licensing service (and, later,
/// the Community Edition's Modules page) can respond precisely — an expired activation token is a
/// re-verify prompt, a bad signature is a rejection, an unknown key id is an operator/config issue.
/// </summary>
public enum LicenceTokenFailure
{
    /// <summary>The token is not a well-formed <c>v4.public</c> token, or its footer is unreadable.</summary>
    Malformed,

    /// <summary>The token names a key id this verifier has no public key for (unknown/rotated-out/foreign).</summary>
    UnknownKeyId,

    /// <summary>The signature did not verify against the named key — forged or corrupted.</summary>
    BadSignature,

    /// <summary>The signature is valid but the token is past its <c>exp</c> claim.</summary>
    Expired,

    /// <summary>The signature is valid but the token's <c>nbf</c> claim is in the future.</summary>
    NotYetValid,

    /// <summary>The signature is valid but the payload could not be read as the expected claims shape.</summary>
    UnreadableClaims,
}

/// <summary>
/// The outcome of verifying a licence token: on success the deserialized <typeparamref name="TClaims"/>
/// and the key id that signed it; on failure a specific <see cref="LicenceTokenFailure"/>. A value type
/// so verification never allocates a result on the hot path and callers must handle both arms.
/// </summary>
public readonly struct LicenceTokenVerification<TClaims>
    where TClaims : notnull
{
    private LicenceTokenVerification(bool isValid, TClaims? claims, string? keyId, LicenceTokenFailure? failure)
    {
        IsValid = isValid;
        Claims = claims;
        KeyId = keyId;
        Failure = failure;
    }

    public bool IsValid { get; }

    /// <summary>The verified claims — non-null iff <see cref="IsValid"/>.</summary>
    public TClaims? Claims { get; }

    /// <summary>The signing key id read from the token footer (available even on most failures).</summary>
    public string? KeyId { get; }

    /// <summary>The failure reason — non-null iff not <see cref="IsValid"/>.</summary>
    public LicenceTokenFailure? Failure { get; }

    public static LicenceTokenVerification<TClaims> Valid(TClaims claims, string keyId) =>
        new(true, claims, keyId, null);

    public static LicenceTokenVerification<TClaims> Invalid(LicenceTokenFailure failure, string? keyId = null) =>
        new(false, default, keyId, failure);
}

/// <summary>
/// Verifies licence tokens against the pinned public keys, checking the signature and any temporal
/// claims. Verification needs only public keys, so this is the half the self-hosted Community Edition
/// will run offline (a later wave); the hosted licensing service runs it too, during call-home.
/// (docs/19-marketplace-licensing.md, ADR-036)
/// </summary>
public interface ILicenceTokenVerifier
{
    LicenceTokenVerification<TClaims> Verify<TClaims>(string token)
        where TClaims : notnull;
}
