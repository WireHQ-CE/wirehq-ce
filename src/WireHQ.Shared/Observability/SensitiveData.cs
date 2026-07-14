namespace WireHQ.Shared.Observability;

/// <summary>
/// The canonical "this field name holds a secret" denylist — the single source of truth shared by every
/// place that has to scrub secrets out of data leaving an in-memory boundary: the telemetry redaction policy
/// (logs/spans, docs/15 §4) and the audit change-diff capture (the durable Postgres audit plane, docs/15 §5).
/// Keeping one list means a newly-recognised secret name is closed off everywhere at once.
/// </summary>
public static class SensitiveData
{
    public const string Mask = "***REDACTED***";

    // Field-name fragments whose VALUE is always a credential/secret. Matched case-insensitively as a
    // substring, so "password" catches "Password"/"DbPassword"/"PasswordHash", "secret" catches
    // "WebhookSecret"/"ClientSecret", "privatekey" catches "PrivateKeyEncrypted". "key" alone is intentionally
    // absent — it is too broad (it would mask "NetworkId"-style fields); specific secret key kinds are listed.
    private static readonly string[] NameFragments =
    [
        "password", "passphrase", "secret", "token", "apikey", "api_key", "privatekey", "private_key",
        "presharedkey", "preshared_key", "psk", "signingkey", "signing_key", "credential", "authorization",
        "connectionstring", "connection_string", "cookie",
    ];

    /// <summary>True if a property/field with this name should have its value fully redacted.</summary>
    public static bool IsSensitiveName(string name)
    {
        foreach (var fragment in NameFragments)
        {
            if (name.Contains(fragment, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
