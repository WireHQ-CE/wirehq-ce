using NSec.Cryptography;
using WireHQ.Application.Abstractions;
using WireHQ.Application.Abstractions.Security;

namespace WireHQ.Licensing.UnitTests;

/// <summary>A fixed clock for deterministic temporal-claim tests.</summary>
internal sealed class FixedClock(DateTimeOffset now) : IDateTimeProvider
{
    public DateTimeOffset UtcNow { get; } = now;
}

/// <summary>
/// A pass-through secret protector for tests — the "protected" private key is just its base64 seed. The
/// real <c>AesGcmSecretProtector</c> is exercised in its own Identity tests; here we isolate key-ring
/// logic from the protection scheme.
/// </summary>
internal sealed class PassthroughSecretProtector : ISecretProtector
{
    public string Protect(string plaintext) => plaintext;

    public string Unprotect(string ciphertext) => ciphertext;
}

/// <summary>Generates Ed25519 test key material via NSec, in the raw base64 form the key ring expects.</summary>
internal static class Ed25519TestKeys
{
    public static LicensingKeyEntry NewEntry(string kid)
    {
        using var key = Key.Create(
            SignatureAlgorithm.Ed25519,
            new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });

        var seed = key.Export(KeyBlobFormat.RawPrivateKey);
        var publicKey = key.PublicKey.Export(KeyBlobFormat.RawPublicKey);

        return new LicensingKeyEntry
        {
            Kid = kid,
            PublicKey = Convert.ToBase64String(publicKey),
            // The pass-through protector means the "protected" material is the plain base64 seed.
            PrivateKeyProtected = Convert.ToBase64String(seed),
        };
    }

    /// <summary>A single-key signing ring under a fixed kid.</summary>
    public static LicensingKeyRing SigningRing(string kid = "test-key-1")
    {
        var options = new LicensingKeyOptions { ActiveKeyId = kid, Keys = [NewEntry(kid)] };
        return LicensingKeyRing.Create(options, new PassthroughSecretProtector());
    }
}
