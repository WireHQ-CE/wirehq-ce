using WireHQ.Domain.Common;

namespace WireHQ.Domain.Sessions;

/// <summary>
/// The kind of second factor. Modeled polymorphically so passkeys/WebAuthn and hardware keys
/// slot in later behind the same abstraction without a schema redesign. (docs/04-security.md)
/// </summary>
public enum MfaCredentialType
{
    Totp = 0,
    WebAuthn = 1,
    HardwareKey = 2,
}

/// <summary>
/// A registered multi-factor credential. For TOTP, <see cref="Secret"/> holds the
/// envelope-encrypted shared secret; for WebAuthn it holds the encoded public key/credential
/// id. The domain treats it as opaque protected material — encryption is an Infrastructure
/// concern.
/// </summary>
public sealed class MfaCredential : Entity
{
    // EF Core
    private MfaCredential()
    {
    }

    private MfaCredential(Guid id, Guid userId, MfaCredentialType type, string secret, string? label)
        : base(id)
    {
        UserId = userId;
        Type = type;
        Secret = secret;
        Label = label;
        CreatedAtUtc = DateTimeOffset.UtcNow;
    }

    public Guid UserId { get; private set; }

    public MfaCredentialType Type { get; private set; }

    /// <summary>Protected material (encrypted at rest). Never logged or returned to clients.</summary>
    public string Secret { get; private set; } = null!;

    public string? Label { get; private set; }

    public bool IsConfirmed { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset? ConfirmedAtUtc { get; private set; }

    public DateTimeOffset? LastUsedAtUtc { get; private set; }

    public static MfaCredential CreateTotp(Guid userId, string encryptedSecret, string? label = null) =>
        new(Guid.CreateVersion7(), userId, MfaCredentialType.Totp, encryptedSecret, label);

    public void Confirm(DateTimeOffset nowUtc)
    {
        IsConfirmed = true;
        ConfirmedAtUtc = nowUtc;
    }

    public void MarkUsed(DateTimeOffset nowUtc) => LastUsedAtUtc = nowUtc;
}
