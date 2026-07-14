using WireHQ.Domain.Common;

namespace WireHQ.Modules.WireGuard.Domain;

/// <summary>
/// Encrypted secret store for a private or preshared key. The secret is held ONLY as ciphertext
/// (AES-256-GCM via the platform's ISecretProtector); the public key (for private kinds) is stored
/// in clear. Lifecycle: Active → Rotating → Revoked. Every transition is audited. (docs/11 §5)
/// </summary>
public sealed class KeyMaterial : Entity, ITenantOwned
{
    public const string Algorithm = "curve25519";

    private KeyMaterial()
    {
    }

    private KeyMaterial(Guid id, Guid organizationId, KeyOwnerType ownerType, Guid ownerId, KeyKind kind, string ciphertext, string? publicKey)
        : base(id)
    {
        OrganizationId = organizationId;
        OwnerType = ownerType;
        OwnerId = ownerId;
        Kind = kind;
        Ciphertext = ciphertext;
        PublicKey = publicKey;
        Status = KeyStatus.Active;
        CreatedAtUtc = DateTimeOffset.UtcNow;
        Version = 1;
    }

    public Guid OrganizationId { get; private set; }
    public KeyOwnerType OwnerType { get; private set; }
    public Guid OwnerId { get; private set; }
    public KeyKind Kind { get; private set; }

    /// <summary>Envelope-encrypted secret. Never logged or returned to clients.</summary>
    public string Ciphertext { get; private set; } = null!;

    /// <summary>Public key for <see cref="KeyKind.Private"/> material; null for preshared keys.</summary>
    public string? PublicKey { get; private set; }

    public KeyStatus Status { get; private set; }
    public int Version { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset? RotatedAtUtc { get; private set; }
    public DateTimeOffset? RevokedAtUtc { get; private set; }

    public static KeyMaterial CreatePrivate(Guid organizationId, KeyOwnerType ownerType, Guid ownerId, string ciphertext, string publicKey) =>
        new(Guid.CreateVersion7(), organizationId, ownerType, ownerId, KeyKind.Private, ciphertext, publicKey);

    public static KeyMaterial CreatePreshared(Guid organizationId, KeyOwnerType ownerType, Guid ownerId, string ciphertext) =>
        new(Guid.CreateVersion7(), organizationId, ownerType, ownerId, KeyKind.Preshared, ciphertext, publicKey: null);

    public void MarkRotating(DateTimeOffset nowUtc)
    {
        Status = KeyStatus.Rotating;
        RotatedAtUtc = nowUtc;
    }

    public void Revoke(DateTimeOffset nowUtc)
    {
        Status = KeyStatus.Revoked;
        RevokedAtUtc = nowUtc;
    }
}
