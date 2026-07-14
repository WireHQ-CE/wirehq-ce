namespace WireHQ.Application.Abstractions;

/// <summary>
/// The authenticated principal for the current request, projected from the validated access
/// token + active membership. Implemented in the API layer over <c>HttpContext</c>. Handlers
/// depend on this, never on ASP.NET.
/// </summary>
public interface ICurrentUser
{
    bool IsAuthenticated { get; }

    Guid? UserId { get; }

    Guid? MembershipId { get; }

    string? Email { get; }

    Guid? SessionId { get; }

    /// <summary>True once the session has satisfied MFA (used by step-up policies).</summary>
    bool MfaSatisfied { get; }

    /// <summary>Effective permission keys in the active organization.</summary>
    IReadOnlyCollection<string> Permissions { get; }

    bool HasPermission(string permission);

    /// <summary>Platform-operator role name (e.g. <c>SuperAdmin</c>), or null for normal users.</summary>
    string? PlatformRole { get; }

    /// <summary>True when the caller is a platform Super Admin (above all org roles).</summary>
    bool IsPlatformAdmin { get; }

    /// <summary>
    /// True when the caller holds any platform-operator tier — Super Admin <b>or</b> the lower Support
    /// role. Gates read-mostly cross-tenant diagnostics (e.g. the platform audit read); mutation still
    /// requires <see cref="IsPlatformAdmin"/>. (docs/15 §10, ADR-032)
    /// </summary>
    bool IsPlatformOperator { get; }

    /// <summary>When impersonating, the platform operator acting as this account (for audit + the UI banner).</summary>
    Guid? ImpersonatorUserId { get; }

    /// <summary>The API key id when the request is authenticated by an API key rather than a user session
    /// (docs/26-api-keys-webhooks.md §5); null for a normal session. A key carries no <c>UserId</c>; this is how
    /// <c>AuditWriter</c> marks a key's actions with the distinct <c>api_key</c> actor type (recording the specific
    /// key on the audit entry is a follow-up — see docs/26 §5). Defaulted so existing implementations need no change.</summary>
    Guid? ApiKeyId => null;
}
