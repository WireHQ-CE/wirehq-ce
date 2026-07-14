using System.Text.Json;
using NSec.Cryptography;
using WireHQ.Application.Updates;
using WireHQ.Licensing.Paseto;

namespace WireHQ.Licensing;

/// <summary>
/// Signs / verifies the CE update manifest (docs/30 §5) as a PASETO v4.public token — reusing the codebase's
/// existing Ed25519 (libsodium/NSec) primitives rather than rolling a bespoke JSON-signing scheme (which would
/// have canonicalisation pitfalls). The manifest is published SIGNED by a <b>dedicated update key</b> (NOT the
/// licensing key, so update checks never depend on licensing being configured); the Community Edition verifies it
/// against a pinned update public key baked into its image. Callers pass only strings — NSec stays inside here.
/// (docs/30 U-4)
/// </summary>
public static class UpdateManifestCodec
{
    // Web defaults (camelCase properties, case-insensitive). The severity enum carries its own
    // [JsonStringEnumConverter] attribute, so "high" parses case-insensitively without a converter here.
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Signs a manifest into a v4.public token (WireHQ-side + tests). The signing key is Ed25519.</summary>
    public static string Sign(UpdateManifest manifest, Key signingKey)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(signingKey);
        var payload = JsonSerializer.SerializeToUtf8Bytes(manifest, Json);
        return PasetoV4Public.Sign(payload, [], signingKey);
    }

    /// <summary>
    /// Signs a manifest with the base64 raw Ed25519 private seed produced by the update-key ceremony
    /// (the <c>--sign-update-manifest</c> release step, docs/30 §5). The seed lives in offline WireHQ custody
    /// (a CI secret), never in a deployment — so this overload takes the raw seed rather than a configured key ring.
    /// NSec stays inside the codec. Throws if the seed is not valid base64 of an Ed25519 private key.
    /// </summary>
    public static string Sign(UpdateManifest manifest, string signingSeedBase64)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (string.IsNullOrWhiteSpace(signingSeedBase64))
        {
            throw new ArgumentException("A base64 Ed25519 private seed is required.", nameof(signingSeedBase64));
        }

        using var key = Key.Import(
            SignatureAlgorithm.Ed25519, Convert.FromBase64String(signingSeedBase64), KeyBlobFormat.RawPrivateKey);
        return Sign(manifest, key);
    }

    /// <summary>
    /// Verifies <paramref name="token"/> against the base64 Ed25519 public key and returns the manifest, or false
    /// on any failure (empty input, bad key, bad signature, unreadable payload). A failure is treated by the caller
    /// as "no trustworthy manifest" — never a false all-clear.
    /// </summary>
    public static bool TryVerify(string? token, string? publicKeyBase64, out UpdateManifest? manifest)
    {
        manifest = null;
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(publicKeyBase64))
        {
            return false;
        }

        PublicKey publicKey;
        try
        {
            publicKey = PublicKey.Import(
                SignatureAlgorithm.Ed25519, Convert.FromBase64String(publicKeyBase64), KeyBlobFormat.RawPublicKey);
        }
        catch (Exception)
        {
            return false; // Malformed / wrong-length public key.
        }

        if (!PasetoV4Public.TryVerify(token, publicKey, out var message))
        {
            return false; // Bad signature / malformed token / foreign key.
        }

        try
        {
            manifest = JsonSerializer.Deserialize<UpdateManifest>(message, Json);
        }
        catch (JsonException)
        {
            return false;
        }

        return manifest is not null && !string.IsNullOrWhiteSpace(manifest.LatestVersion);
    }
}
