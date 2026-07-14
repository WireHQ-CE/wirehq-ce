namespace WireHQ.Licensing;

/// <summary>
/// The licence-signing key ring, bound from the <c>Licensing</c> configuration section. Supports key
/// rotation from day one (D-7): many keys may be present — each identified by a <c>Kid</c> — while one
/// is <see cref="ActiveKeyId">active</see> for signing new tokens. Verification tries the key named in
/// each token, so rotating the active key never strands tokens signed by an earlier one.
///
/// Public keys are plain base64 (they are not secret). A private key is stored
/// <see cref="LicensingKeyEntry.PrivateKeyProtected">SecretProtection-encrypted</see> and is present
/// only on the hosted signer — a self-hosted verifier configures public keys alone. A production
/// deployment can later swap the signer for a KMS/HSM implementation behind
/// <c>ILicenceTokenSigner</c> without changing this shape.
/// </summary>
public sealed class LicensingKeyOptions
{
    public const string SectionName = "Licensing";

    /// <summary>The <c>Kid</c> of the key new tokens are signed with. Must match one of <see cref="Keys"/>.</summary>
    public string ActiveKeyId { get; set; } = string.Empty;

    public List<LicensingKeyEntry> Keys { get; set; } = [];
}

/// <summary>One entry in the licence-signing key ring — a key id, its public key, and (signer only) its
/// protected private key.</summary>
public sealed class LicensingKeyEntry
{
    /// <summary>A short, stable key id carried in the token footer to select this key at verification.</summary>
    public string Kid { get; set; } = string.Empty;

    /// <summary>The raw 32-byte Ed25519 public key, base64-encoded. Not secret.</summary>
    public string PublicKey { get; set; } = string.Empty;

    /// <summary>
    /// The raw 32-byte Ed25519 private seed, base64-encoded and then <c>ISecretProtector</c>-encrypted.
    /// Present only where signing happens (the hosted service); omit it for a verify-only deployment.
    /// </summary>
    public string? PrivateKeyProtected { get; set; }
}
