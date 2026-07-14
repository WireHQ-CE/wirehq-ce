using NSec.Cryptography;
using WireHQ.Application.Abstractions.Security;

namespace WireHQ.Licensing;

/// <summary>
/// The materialized licence-signing key ring: the imported Ed25519 public keys (by <c>kid</c>) used for
/// verification, and — where configured — the active private key used for signing. Built once from
/// <see cref="LicensingKeyOptions"/> and held as a singleton; owns the unmanaged NSec key handle, so it
/// is disposable.
///
/// Construction succeeds with public keys alone (a verify-only deployment, e.g. a future self-hosted
/// install); signing is only possible when the active entry carries a private key, in which case the
/// key is decrypted via <see cref="ISecretProtector"/> at startup and never re-read from configuration.
/// </summary>
public sealed class LicensingKeyRing : IDisposable
{
    private static readonly SignatureAlgorithm Algorithm = SignatureAlgorithm.Ed25519;

    private readonly Dictionary<string, PublicKey> _publicKeys;
    private readonly Key? _activeSigningKey;

    private LicensingKeyRing(string activeKeyId, Dictionary<string, PublicKey> publicKeys, Key? activeSigningKey)
    {
        ActiveKeyId = activeKeyId;
        _publicKeys = publicKeys;
        _activeSigningKey = activeSigningKey;
    }

    /// <summary>The key id new tokens are signed with (and that a signer stamps into token footers).</summary>
    public string ActiveKeyId { get; }

    /// <summary>Whether this ring can sign (its active entry carried a private key).</summary>
    public bool CanSign => _activeSigningKey is not null;

    public static LicensingKeyRing Create(LicensingKeyOptions options, ISecretProtector secretProtector)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(secretProtector);

        if (string.IsNullOrWhiteSpace(options.ActiveKeyId))
        {
            throw new InvalidOperationException("Licensing:ActiveKeyId must be configured.");
        }

        if (options.Keys.Count == 0)
        {
            throw new InvalidOperationException("Licensing:Keys must contain at least one key.");
        }

        var publicKeys = new Dictionary<string, PublicKey>(StringComparer.Ordinal);
        foreach (var entry in options.Keys)
        {
            if (string.IsNullOrWhiteSpace(entry.Kid))
            {
                throw new InvalidOperationException("Every Licensing:Keys entry must have a Kid.");
            }

            if (publicKeys.ContainsKey(entry.Kid))
            {
                throw new InvalidOperationException($"Duplicate Licensing key id '{entry.Kid}'.");
            }

            publicKeys[entry.Kid] = ImportPublicKey(entry);
        }

        if (!publicKeys.ContainsKey(options.ActiveKeyId))
        {
            throw new InvalidOperationException($"Licensing:ActiveKeyId '{options.ActiveKeyId}' is not among Licensing:Keys.");
        }

        var activeEntry = options.Keys.First(k => k.Kid == options.ActiveKeyId);
        var activeSigningKey = ImportSigningKey(activeEntry, secretProtector);

        return new LicensingKeyRing(options.ActiveKeyId, publicKeys, activeSigningKey);
    }

    public bool TryGetPublicKey(string keyId, out PublicKey publicKey) =>
        _publicKeys.TryGetValue(keyId, out publicKey!);

    /// <summary>The active private key for signing; throws if this ring is verify-only.</summary>
    public Key ActiveSigningKey =>
        _activeSigningKey
        ?? throw new InvalidOperationException(
            $"Licensing key '{ActiveKeyId}' has no private key configured — this deployment can verify but not sign.");

    private static PublicKey ImportPublicKey(LicensingKeyEntry entry)
    {
        var bytes = DecodeBase64(entry.PublicKey, $"Licensing:Keys['{entry.Kid}'].PublicKey");
        try
        {
            return PublicKey.Import(Algorithm, bytes, KeyBlobFormat.RawPublicKey);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException(
                $"Licensing:Keys['{entry.Kid}'].PublicKey is not a valid raw Ed25519 public key (expected 32 bytes).", ex);
        }
    }

    private static Key? ImportSigningKey(LicensingKeyEntry entry, ISecretProtector secretProtector)
    {
        if (string.IsNullOrWhiteSpace(entry.PrivateKeyProtected))
        {
            return null; // Verify-only for this key.
        }

        string seedBase64;
        try
        {
            seedBase64 = secretProtector.Unprotect(entry.PrivateKeyProtected);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Licensing:Keys['{entry.Kid}'].PrivateKeyProtected could not be decrypted — check SecretProtection:Key.", ex);
        }

        var seed = DecodeBase64(seedBase64, $"Licensing:Keys['{entry.Kid}'] private seed");
        try
        {
            return Key.Import(Algorithm, seed, KeyBlobFormat.RawPrivateKey);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException(
                $"Licensing:Keys['{entry.Kid}'] private key is not a valid raw Ed25519 seed (expected 32 bytes).", ex);
        }
    }

    private static byte[] DecodeBase64(string value, string label)
    {
        try
        {
            return Convert.FromBase64String(value);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException($"{label} is not valid base64.", ex);
        }
    }

    public void Dispose() => _activeSigningKey?.Dispose();
}
