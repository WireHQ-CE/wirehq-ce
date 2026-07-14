using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using WireHQ.Application.Abstractions.Security;

namespace WireHQ.Identity.Protection;

public sealed class SecretProtectionOptions
{
    public const string SectionName = "SecretProtection";

    /// <summary>Base64-encoded 256-bit key. From a KMS in production; a local secret self-hosted.</summary>
    public string Key { get; set; } = string.Empty;
}

/// <summary>
/// AES-256-GCM authenticated encryption for sensitive columns (TOTP secrets, provider tokens).
/// This is the local implementation of the <see cref="ISecretProtector"/> envelope-encryption
/// port; a managed-KMS implementation swaps in behind the same interface for SaaS. Output layout:
/// <c>base64(nonce ‖ tag ‖ ciphertext)</c>. (docs/04-security.md)
/// </summary>
public sealed class AesGcmSecretProtector : ISecretProtector
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private readonly byte[] _key;

    public AesGcmSecretProtector(IOptions<SecretProtectionOptions> options)
    {
        var key = options.Value.Key;
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new InvalidOperationException("SecretProtection:Key must be configured (base64, 32 bytes).");
        }

        _key = Convert.FromBase64String(key);
        if (_key.Length != 32)
        {
            throw new InvalidOperationException("SecretProtection:Key must decode to exactly 32 bytes.");
        }
    }

    public string Protect(string plaintext)
    {
        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var ciphertext = new byte[plaintextBytes.Length];
        var tag = new byte[TagSize];

        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, plaintextBytes, ciphertext, tag);

        var output = new byte[NonceSize + TagSize + ciphertext.Length];
        Buffer.BlockCopy(nonce, 0, output, 0, NonceSize);
        Buffer.BlockCopy(tag, 0, output, NonceSize, TagSize);
        Buffer.BlockCopy(ciphertext, 0, output, NonceSize + TagSize, ciphertext.Length);
        return Convert.ToBase64String(output);
    }

    public string Unprotect(string ciphertext)
    {
        var input = Convert.FromBase64String(ciphertext);
        var nonce = input.AsSpan(0, NonceSize);
        var tag = input.AsSpan(NonceSize, TagSize);
        var encrypted = input.AsSpan(NonceSize + TagSize);

        var plaintext = new byte[encrypted.Length];
        using var aes = new AesGcm(_key, TagSize);
        aes.Decrypt(nonce, encrypted, tag, plaintext);
        return Encoding.UTF8.GetString(plaintext);
    }
}
