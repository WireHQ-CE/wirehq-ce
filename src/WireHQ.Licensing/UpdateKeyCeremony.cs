using NSec.Cryptography;

namespace WireHQ.Licensing;

/// <summary>
/// The update-manifest signing-key ceremony (docs/30 §5, U-4): generates a fresh Ed25519 key pair for signing the
/// Community Edition update manifest. This is a <b>dedicated</b> key, distinct from the licensing key, so update
/// checks never depend on licensing being configured.
///
/// Unlike <see cref="LicensingKeyCeremony"/>, the private half is printed as a <b>plaintext</b> base64 seed — it is
/// NOT wrapped with any <c>SecretProtection:Key</c>, because the manifest is signed <b>offline</b> (a release-pipeline
/// / laptop step, e.g. <c>--sign-update-manifest</c>), never by a running deployment. Keep the seed in offline custody
/// (a CI secret / password manager); it never belongs in a deployment's config. The public half is baked into the CE
/// image (<c>Updates:PublicKey</c> / <c>UPDATES_PUBLIC_KEY</c>) so verification is a build constant.
///
/// Run via the host verb (the <c>--generate-licensing-key</c> pattern):
/// <code>dotnet WireHQ.Api.dll --generate-update-key</code>
/// </summary>
public static class UpdateKeyCeremony
{
    public static string GenerateConfigBlock(DateTimeOffset nowUtc)
    {
        using var key = Key.Create(
            SignatureAlgorithm.Ed25519,
            new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });

        var publicKey = Convert.ToBase64String(key.PublicKey.Export(KeyBlobFormat.RawPublicKey));
        var privateSeed = Convert.ToBase64String(key.Export(KeyBlobFormat.RawPrivateKey));

        return $"""
            Update-manifest signing key generated ({nowUtc:u}).

            PUBLIC key — bake into the CE image so installs can verify the manifest (a build constant):
              Updates__PublicKey={publicKey}
              (deploy/.env form: UPDATES_PUBLIC_KEY={publicKey})

            PRIVATE seed — KEEP OFFLINE. This is PLAINTEXT (not encrypted): store it in a secret manager / CI
            secret (e.g. UPDATE_SIGNING_SEED) and NEVER put it in a deployment's config. It signs the manifest
            via `--sign-update-manifest`:
              UPDATE_SIGNING_SEED={privateSeed}

            This is a DEDICATED update key, separate from the licensing key — losing/rotating it only affects
            update-notification signing (re-bake the new public key into the next CE image).
            """;
    }
}
