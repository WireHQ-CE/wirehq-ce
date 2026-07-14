using NSec.Cryptography;
using WireHQ.Application.Abstractions.Security;

namespace WireHQ.Licensing;

/// <summary>
/// The licence-signing key ceremony (D-7, docs/19-marketplace-licensing.md §3): generates a fresh
/// Ed25519 key pair and prints ready-to-paste configuration — the <c>kid</c>, the plain base64 public
/// key, and the private seed <b>already encrypted</b> with the deployment's <c>SecretProtection:Key</c>
/// via <see cref="ISecretProtector"/>. The plaintext seed exists only in this process's memory; it is
/// never written anywhere.
///
/// Run via the host verb (the <c>--migrate</c> pattern) in an environment where the target
/// <c>SecretProtection:Key</c> is configured:
/// <code>dotnet WireHQ.Api.dll --generate-licensing-key</code>
/// Rotation = run again, add the new entry alongside the old keys, and switch <c>ActiveKeyId</c> —
/// verifiers keep every listed public key, so previously issued licences stay valid (docs/19 §3).
/// </summary>
public static class LicensingKeyCeremony
{
    public static string GenerateConfigBlock(ISecretProtector secretProtector, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(secretProtector);

        using var key = Key.Create(
            SignatureAlgorithm.Ed25519,
            new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });

        var publicKey = Convert.ToBase64String(key.PublicKey.Export(KeyBlobFormat.RawPublicKey));
        var protectedSeed = secretProtector.Protect(Convert.ToBase64String(key.Export(KeyBlobFormat.RawPrivateKey)));

        // A dated, unique key id — sortable and human-readable in token footers and licence rows.
        var kid = $"lk-{nowUtc:yyyyMMdd}-{Guid.NewGuid().ToString("N")[..6]}";

        return $"""
            Licensing signing key generated ({nowUtc:u}).
            The private seed below is ALREADY ENCRYPTED with this environment's SecretProtection:Key —
            it can only be used by a deployment holding the same key. The plaintext seed was never persisted.

            Environment-variable form (append the index for additional keys; set ActiveKeyId to rotate):

              Licensing__ActiveKeyId={kid}
              Licensing__Keys__0__Kid={kid}
              Licensing__Keys__0__PublicKey={publicKey}
              Licensing__Keys__0__PrivateKeyProtected={protectedSeed}

            Verify-only deployments (e.g. a future self-hosted install) configure Kid + PublicKey only.
            """;
    }
}
