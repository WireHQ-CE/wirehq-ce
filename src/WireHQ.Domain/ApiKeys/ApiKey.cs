using WireHQ.Domain.Common;
using WireHQ.Shared.Results;

namespace WireHQ.Domain.ApiKeys;

/// <summary>
/// A per-organization API key (docs/26-api-keys-webhooks.md, ADR-043): a long-lived, <b>scoped</b> and
/// <b>revocable</b> bearer secret that lets a script/CI/service authenticate to the WireHQ API without a human
/// login. Only the SHA-256 <see cref="KeyHash"/> is stored (the SCIM-token precedent — the plaintext is shown
/// once), plus a short display <see cref="KeyPrefix"/>. Scopes are permission keys from the RBAC catalog, so a
/// key's principal carries the same <c>perm</c> claims as a session and the whole authorization pipeline works
/// unchanged. Tenant-owned in the reused <c>identity</c> schema (RLS for free). Entitlement-gated core
/// (<c>api.keys</c>) — usable in every edition, no CE strip.
/// </summary>
public sealed class ApiKey : AggregateRoot, ITenantOwned, IAuditable
{
    public const int MaxNameLength = 128;
    public const int MaxScopes = 64;

    private readonly List<ApiKeyScope> _scopes = [];

    // EF Core
    private ApiKey()
    {
    }

    private ApiKey(Guid id, Guid organizationId, string name, string keyPrefix, string keyHash, Guid? createdByUserId, DateTimeOffset? expiresAtUtc)
        : base(id)
    {
        OrganizationId = organizationId;
        Name = name;
        KeyPrefix = keyPrefix;
        KeyHash = keyHash;
        CreatedByUserId = createdByUserId;
        ExpiresAtUtc = expiresAtUtc;
        Status = ApiKeyStatus.Active;
    }

    public Guid OrganizationId { get; private set; }

    public string Name { get; private set; } = null!;

    /// <summary>The first few characters of the plaintext key (e.g. <c>whq_ab12cd…</c>) — shown in lists so a key
    /// is recognisable without revealing it.</summary>
    public string KeyPrefix { get; private set; } = null!;

    /// <summary>SHA-256 (base64) of the plaintext key. The secret itself is never stored.</summary>
    public string KeyHash { get; private set; } = null!;

    public Guid? CreatedByUserId { get; private set; }

    public DateTimeOffset? ExpiresAtUtc { get; private set; }

    public DateTimeOffset? LastUsedAtUtc { get; private set; }

    public ApiKeyStatus Status { get; private set; }

    public IReadOnlyCollection<ApiKeyScope> Scopes => _scopes.AsReadOnly();

    // IAuditable
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public Guid? CreatedBy { get; private set; }
    public DateTimeOffset? UpdatedAtUtc { get; private set; }
    public Guid? UpdatedBy { get; private set; }

    /// <summary>Create a key from a validated name + already-generated prefix/hash + its granted scopes. The caller
    /// (the create handler) generates the plaintext and validates that every scope is grantable by the actor.</summary>
    public static Result<ApiKey> Create(
        Guid organizationId, string name, string keyPrefix, string keyHash,
        IReadOnlyCollection<string> scopes, Guid? createdByUserId, DateTimeOffset? expiresAtUtc)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > MaxNameLength)
        {
            return ApiKeyErrors.InvalidName;
        }

        if (scopes.Count == 0)
        {
            return ApiKeyErrors.NoScopes;
        }

        if (scopes.Count > MaxScopes)
        {
            return ApiKeyErrors.TooManyScopes;
        }

        var key = new ApiKey(Guid.CreateVersion7(), organizationId, name.Trim(), keyPrefix, keyHash, createdByUserId, expiresAtUtc);
        foreach (var scope in scopes.Distinct(StringComparer.Ordinal))
        {
            key._scopes.Add(new ApiKeyScope(key.Id, scope));
        }

        return key;
    }

    /// <summary>Active and not expired — the check the authentication scheme makes.</summary>
    public bool IsUsable(DateTimeOffset nowUtc) =>
        Status == ApiKeyStatus.Active && (ExpiresAtUtc is null || ExpiresAtUtc > nowUtc);

    public void Revoke() => Status = ApiKeyStatus.Revoked;

    public void MarkUsed(DateTimeOffset atUtc) => LastUsedAtUtc = atUtc;
}

/// <summary>One granted scope — a permission key from the RBAC catalog. A NORMAL child entity keyed by
/// <see cref="ApiKeyId"/> (the RolePermission/SsoRoleMapping lesson — dodges the owned-collection append gotcha).</summary>
public sealed class ApiKeyScope
{
    // EF Core
    private ApiKeyScope()
    {
    }

    public ApiKeyScope(Guid apiKeyId, string permissionKey)
    {
        ApiKeyId = apiKeyId;
        PermissionKey = permissionKey;
    }

    public Guid ApiKeyId { get; private set; }

    public string PermissionKey { get; private set; } = null!;
}

public enum ApiKeyStatus
{
    Active = 0,
    Revoked = 1,
}

public static class ApiKeyErrors
{
    public static readonly Error InvalidName =
        Error.Validation("api_key.invalid_name", "A name is required (128 characters or fewer).");

    public static readonly Error NoScopes =
        Error.Validation("api_key.no_scopes", "Select at least one scope for the key.");

    public static readonly Error TooManyScopes =
        Error.Validation("api_key.too_many_scopes", "A key can grant at most 64 scopes.");

    public static readonly Error UnknownScope =
        Error.Validation("api_key.unknown_scope", "One or more selected scopes are not valid permissions.");

    public static readonly Error ScopeNotGrantable =
        Error.Forbidden("api_key.scope_not_grantable", "You can only grant a key scopes you hold yourself.");

    public static readonly Error NotFound =
        Error.NotFound("api_key.not_found", "API key was not found.");

    public static readonly Error InvalidExpiry =
        Error.Validation("api_key.invalid_expiry", "The expiry date must be in the future.");
}
