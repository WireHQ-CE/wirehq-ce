using WireHQ.Modules.WireGuard.Domain;

namespace WireHQ.Modules.WireGuard.Services;

/// <summary>
/// The Key Management layer (docs/11-wireguard-module.md §5). Generates Curve25519 keypairs and
/// preshared keys, persists private/PSK material ONLY as ciphertext (envelope encryption), and
/// reveals plaintext just-in-time for config rendering/export. Stored material is added to the
/// current unit of work; the caller's UnitOfWork behavior commits it.
/// </summary>
public interface IKeyManagementService
{
    /// <summary>
    /// Generates a keypair, stores the private key encrypted as <see cref="KeyMaterial"/>, and returns
    /// the public key + the new key id + the plaintext private key. The plaintext is for immediate
    /// config rendering only — never persist or log it.
    /// </summary>
    StoredKeyPair GenerateAndStoreKeyPair(Guid organizationId, KeyOwnerType ownerType, Guid ownerId);

    /// <summary>Generates a preshared key, stores it encrypted, and returns its id + plaintext (once).</summary>
    StoredSecret GenerateAndStorePresharedKey(Guid organizationId, KeyOwnerType ownerType, Guid ownerId);

    /// <summary>Decrypts stored key material for export. The caller enforces permission + audit.</summary>
    Task<string?> RevealAsync(Guid keyMaterialId, CancellationToken cancellationToken);

    /// <summary>Marks all active key material for an owner as revoked (added to the unit of work).</summary>
    Task RevokeForOwnerAsync(KeyOwnerType ownerType, Guid ownerId, DateTimeOffset nowUtc, CancellationToken cancellationToken);

    /// <summary>
    /// Hard-deletes an owner's key material so the encrypted secret no longer exists at rest. Used when an
    /// instance switches to <c>AgentManaged</c> custody — WireHQ must hold no copy of the interface key.
    /// Added to the unit of work; the caller saves.
    /// </summary>
    Task DeleteForOwnerAsync(KeyOwnerType ownerType, Guid ownerId, CancellationToken cancellationToken);
}

public sealed record StoredKeyPair(Guid KeyMaterialId, string PublicKey, string PrivateKey);

public sealed record StoredSecret(Guid KeyMaterialId, string Secret);
