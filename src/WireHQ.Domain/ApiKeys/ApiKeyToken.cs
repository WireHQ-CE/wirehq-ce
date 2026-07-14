using System.Security.Cryptography;
using System.Text;

namespace WireHQ.Domain.ApiKeys;

/// <summary>
/// Generation + hashing for API-key secrets (docs/26-api-keys-webhooks.md §4). The plaintext is a high-entropy,
/// opaque, URL-safe token prefixed <c>whq_</c>; only its SHA-256 hash + a short display prefix are persisted. A
/// plain lookup hash (no salt/work-factor) is sufficient — the token is high-entropy and opaque, not a password
/// (the SCIM-token rationale). Kept-core so the create handler and the authentication scheme hash identically.
/// </summary>
public static class ApiKeyToken
{
    public const string Prefix = "whq_";

    /// <summary>How many leading characters of the plaintext are stored/shown as the display prefix.</summary>
    private const int DisplayPrefixLength = 12; // "whq_" + 8 chars

    public sealed record Generated(string Plaintext, string DisplayPrefix, string Hash);

    /// <summary>Mint a new key: the plaintext (returned to the caller ONCE), its display prefix, and its hash.</summary>
    public static Generated Generate()
    {
        var secret = Base64Url(RandomNumberGenerator.GetBytes(32));
        var plaintext = Prefix + secret;
        return new Generated(plaintext, DisplayPrefixOf(plaintext), Hash(plaintext));
    }

    /// <summary>SHA-256 (base64) of the plaintext key — the stored + lookup hash.</summary>
    public static string Hash(string plaintext) =>
        Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(plaintext)));

    public static string DisplayPrefixOf(string plaintext) =>
        plaintext.Length <= DisplayPrefixLength ? plaintext : plaintext[..DisplayPrefixLength];

    /// <summary>True when a presented credential looks like a WireHQ API key (routes it to the API-key scheme).</summary>
    public static bool LooksLikeApiKey(string? value) =>
        !string.IsNullOrEmpty(value) && value.StartsWith(Prefix, StringComparison.Ordinal);

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
