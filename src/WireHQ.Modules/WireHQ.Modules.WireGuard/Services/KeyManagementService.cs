using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using NSec.Cryptography;
using WireHQ.Application.Abstractions.Persistence;
using WireHQ.Application.Abstractions.Security;
using WireHQ.Modules.WireGuard.Domain;

namespace WireHQ.Modules.WireGuard.Services;

/// <summary>
/// Curve25519 (X25519) keypairs via libsodium (NSec); preshared keys via the system CSPRNG. Private
/// and preshared material is encrypted with the platform's <see cref="ISecretProtector"/> before it
/// touches the database. Output keys are base64 of the raw 32-byte values — interoperable with
/// standard WireGuard.
/// </summary>
public sealed class KeyManagementService(IApplicationDbContext dbContext, ISecretProtector secretProtector)
    : IKeyManagementService
{
    public StoredKeyPair GenerateAndStoreKeyPair(Guid organizationId, KeyOwnerType ownerType, Guid ownerId)
    {
        var (publicKey, privateKey) = GenerateCurve25519KeyPair();

        var keyMaterial = KeyMaterial.CreatePrivate(organizationId, ownerType, ownerId, secretProtector.Protect(privateKey), publicKey);
        dbContext.Set<KeyMaterial>().Add(keyMaterial);

        return new StoredKeyPair(keyMaterial.Id, publicKey, privateKey);
    }

    public StoredSecret GenerateAndStorePresharedKey(Guid organizationId, KeyOwnerType ownerType, Guid ownerId)
    {
        var psk = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

        var keyMaterial = KeyMaterial.CreatePreshared(organizationId, ownerType, ownerId, secretProtector.Protect(psk));
        dbContext.Set<KeyMaterial>().Add(keyMaterial);

        return new StoredSecret(keyMaterial.Id, psk);
    }

    public async Task<string?> RevealAsync(Guid keyMaterialId, CancellationToken cancellationToken)
    {
        var keyMaterial = await dbContext.Set<KeyMaterial>()
            .FirstOrDefaultAsync(k => k.Id == keyMaterialId, cancellationToken);

        return keyMaterial is null ? null : secretProtector.Unprotect(keyMaterial.Ciphertext);
    }

    public async Task RevokeForOwnerAsync(KeyOwnerType ownerType, Guid ownerId, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        var keys = await dbContext.Set<KeyMaterial>()
            .Where(k => k.OwnerType == ownerType && k.OwnerId == ownerId && k.Status != KeyStatus.Revoked)
            .ToListAsync(cancellationToken);

        foreach (var key in keys)
        {
            key.Revoke(nowUtc);
        }
    }

    public async Task DeleteForOwnerAsync(KeyOwnerType ownerType, Guid ownerId, CancellationToken cancellationToken)
    {
        var keys = await dbContext.Set<KeyMaterial>()
            .Where(k => k.OwnerType == ownerType && k.OwnerId == ownerId)
            .ToListAsync(cancellationToken);

        dbContext.Set<KeyMaterial>().RemoveRange(keys);
    }

    private static (string PublicKey, string PrivateKey) GenerateCurve25519KeyPair()
    {
        var algorithm = KeyAgreementAlgorithm.X25519;
        using var key = Key.Create(algorithm, new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });

        var privateKey = key.Export(KeyBlobFormat.RawPrivateKey);
        var publicKey = key.PublicKey.Export(KeyBlobFormat.RawPublicKey);

        return (Convert.ToBase64String(publicKey), Convert.ToBase64String(privateKey));
    }
}
