using System.Text.RegularExpressions;
using WireHQ.Shared.Observability;

namespace WireHQ.Api.Observability;

/// <summary>
/// The default <see cref="IRedactionPolicy"/> (docs/15 §4). Redacts by property name (anything that looks
/// like a credential) and by value shape (secret-shaped substrings inside free text). Deliberately
/// conservative — over-redacting a diagnostic field is a smaller harm than leaking a key.
/// </summary>
public sealed partial class RedactionPolicy : IRedactionPolicy
{
    public const string Mask = SensitiveData.Mask;

    // The sensitive-name denylist is shared with the audit change-diff capture so a secret name is closed off
    // in both planes at once — see <see cref="SensitiveData"/>.
    public bool IsSensitiveProperty(string name) => SensitiveData.IsSensitiveName(name);

    public string Redact(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        text = JwtPattern().Replace(text, Mask);
        text = StripeKeyPattern().Replace(text, Mask);
        text = BearerPattern().Replace(text, $"Bearer {Mask}");
        text = WireGuardKeyPattern().Replace(text, Mask);
        text = ConnectionPasswordPattern().Replace(text, $"$1={Mask}");
        return text;
    }

    // A JWT: three base64url segments. Covers our access tokens + any IdP token that leaks into a log.
    [GeneratedRegex(@"eyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+")]
    private static partial Regex JwtPattern();

    // Stripe API + webhook signing keys.
    [GeneratedRegex(@"\b(?:sk|rk|pk)_(?:live|test)_[A-Za-z0-9]+\b|\bwhsec_[A-Za-z0-9]+\b")]
    private static partial Regex StripeKeyPattern();

    // An Authorization: Bearer <token> value.
    [GeneratedRegex(@"Bearer\s+[A-Za-z0-9._\-]+", RegexOptions.IgnoreCase)]
    private static partial Regex BearerPattern();

    // A WireGuard / Curve25519 key: 32 bytes base64 = 43 chars + '='.
    [GeneratedRegex(@"(?<![A-Za-z0-9+/])[A-Za-z0-9+/]{43}=")]
    private static partial Regex WireGuardKeyPattern();

    // A connection-string password (keep the key, mask the value up to the next ';').
    [GeneratedRegex(@"(?i)\b(password|pwd)=[^;]+")]
    private static partial Regex ConnectionPasswordPattern();
}
