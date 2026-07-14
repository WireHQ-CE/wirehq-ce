namespace WireHQ.Identity.Jwt;

/// <summary>
/// JWT configuration, bound from the <c>Jwt</c> section. The signing key MUST be supplied via
/// environment/secret in non-dev environments (never committed). HS256 is used for a single
/// issuer; the design accommodates RS256 + <c>kid</c> rotation when verifiers are distributed.
/// </summary>
public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    public string Issuer { get; set; } = "https://wirehq.net";

    public string Audience { get; set; } = "wirehq";

    /// <summary>Symmetric signing key (≥ 32 bytes). Provided via secret in real environments.</summary>
    public string SigningKey { get; set; } = string.Empty;

    public int AccessTokenMinutes { get; set; } = 15;

    public int ClockSkewSeconds { get; set; } = 30;
}

/// <summary>Custom JWT claim type names used across issuance and the current-user projection.</summary>
public static class WireHqClaims
{
    public const string OrganizationId = "org";
    public const string MembershipId = "mbr";
    public const string SessionId = "sid";
    public const string Permission = "perm";
    public const string MfaSatisfied = "mfa";
    public const string SecurityStamp = "sst";

    /// <summary>Platform-operator role name (e.g. <c>SuperAdmin</c>); absent for normal users.</summary>
    public const string PlatformRole = "plat";

    /// <summary>When impersonating, the user id of the platform operator acting as this account.</summary>
    public const string Impersonator = "imp";

    /// <summary>The API key id when the request is authenticated by an API key rather than a user session
    /// (docs/26-api-keys-webhooks.md §5). Marks the principal as a key + carries the key for attribution.</summary>
    public const string ApiKeyId = "akid";
}
