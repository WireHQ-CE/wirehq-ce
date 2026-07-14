using WireHQ.Domain.Common;

namespace WireHQ.Domain.Sessions;

/// <summary>
/// A server-side session. Because sessions are tracked here (not just encoded in a JWT),
/// logout, "log out everywhere", admin force-logout, and password-change invalidation are all
/// real, immediate operations rather than "wait for the token to expire". (docs/04-security.md)
/// </summary>
public sealed class UserSession : Entity
{
    // EF Core
    private UserSession()
    {
    }

    private UserSession(Guid id, Guid userId, string? ipAddress, string? userAgent, Guid? impersonatedByUserId, DateTimeOffset nowUtc)
        : base(id)
    {
        UserId = userId;
        IpAddress = ipAddress;
        UserAgent = userAgent;
        ImpersonatedByUserId = impersonatedByUserId;
        CreatedAtUtc = nowUtc;
        LastSeenAtUtc = nowUtc;
    }

    public Guid UserId { get; private set; }

    /// <summary>When set, this is an impersonation session: the platform operator (Super Admin) acting
    /// as <see cref="UserId"/>. Null for ordinary sessions. Powers audit attribution + safe "exit".</summary>
    public Guid? ImpersonatedByUserId { get; private set; }

    public bool IsImpersonation => ImpersonatedByUserId is not null;

    public string? IpAddress { get; private set; }

    public string? UserAgent { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset LastSeenAtUtc { get; private set; }

    public DateTimeOffset? RevokedAtUtc { get; private set; }

    /// <summary>
    /// Whether this session has satisfied MFA. Tracked on the session (not just the token) so a
    /// refresh cannot upgrade a still-pending session into a fully-authorized one — closing the
    /// MFA bypass. Set true at login when MFA isn't required, or on successful MFA verification.
    /// </summary>
    public bool MfaSatisfied { get; private set; }

    public bool IsActive => RevokedAtUtc is null;

    public static UserSession Start(Guid userId, string? ipAddress, string? userAgent, DateTimeOffset nowUtc, Guid? impersonatedByUserId = null) =>
        new(Guid.CreateVersion7(), userId, ipAddress, userAgent, impersonatedByUserId, nowUtc);

    public void Touch(DateTimeOffset nowUtc) => LastSeenAtUtc = nowUtc;

    public void MarkMfaSatisfied() => MfaSatisfied = true;

    public void Revoke(DateTimeOffset nowUtc) => RevokedAtUtc ??= nowUtc;
}
