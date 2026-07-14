using Microsoft.EntityFrameworkCore;
using WireHQ.Application.Abstractions;
using WireHQ.Application.Abstractions.Persistence;
using WireHQ.Application.Abstractions.Security;
using WireHQ.Domain.Identity;
using WireHQ.Domain.Memberships;
using WireHQ.Domain.Sessions;

namespace WireHQ.Application.Features.Authentication;

/// <summary>
/// Centralizes the mechanics of an authenticated session — picking the active tenant, resolving
/// effective permissions, and minting the access/refresh tokens — so Login, MFA-verify, and
/// Refresh all behave identically. Entities are added to the unit of work; the caller's
/// UnitOfWork behavior commits them.
/// </summary>
public sealed class AuthSessionService(
    IApplicationDbContext dbContext,
    ITokenService tokenService,
    IPermissionService permissionService,
    IDateTimeProvider clock)
{
    public const int RefreshTokenLifetimeDays = 30;

    /// <summary>
    /// Hard cap on an impersonation session's lifetime (ADR-032): an impersonation session older than this
    /// (measured from <see cref="WireHQ.Domain.Sessions.UserSession.CreatedAtUtc"/>) is refused at refresh and
    /// revoked, so a support "act-as" session cannot be silently held open by token refresh. Re-entering
    /// impersonation requires a fresh, reason-stamped, audited start. Ordinary sessions are unaffected.
    /// </summary>
    public const int ImpersonationSessionLifetimeMinutes = 30;

    /// <summary>Begin a new session for a freshly-authenticated user (active on their default org).</summary>
    public Task<IssuedSession> StartSessionAsync(
        User user, string? ipAddress, string? userAgent, CancellationToken cancellationToken) =>
        StartSessionAsync(user, membership: null, impersonatedByUserId: null, resolveDefault: true, mfaPreSatisfied: false, ipAddress, userAgent, cancellationToken);

    /// <summary>
    /// Begin a session for a user who has authenticated via an <b>external identity provider</b> (SSO) on a
    /// specific <paramref name="membership"/>. When <paramref name="mfaAlreadySatisfied"/> is true the IdP is
    /// trusted for MFA (the connection's <c>TrustsIdpForMfa</c>) and WireHQ's own second factor is skipped;
    /// otherwise WireHQ's MFA gate still applies. No impersonator — this is the user's own session. (docs/21 §9)
    /// </summary>
    public Task<IssuedSession> StartFederatedSessionAsync(
        User user, Membership membership, bool mfaAlreadySatisfied,
        string? ipAddress, string? userAgent, CancellationToken cancellationToken) =>
        StartSessionAsync(user, membership, impersonatedByUserId: null, resolveDefault: false, mfaPreSatisfied: mfaAlreadySatisfied, ipAddress, userAgent, cancellationToken);

    /// <summary>
    /// Begin an <b>impersonation</b> session: a platform operator (<paramref name="impersonatorUserId"/>)
    /// acting as <paramref name="targetUser"/> within <paramref name="targetMembership"/>'s org. The token
    /// carries the target's org/permissions (so org-scoped RBAC works unchanged) + an impersonator claim,
    /// and never a platform claim — impersonation drops platform privileges. MFA is pre-satisfied (the
    /// operator already authenticated).
    /// </summary>
    public Task<IssuedSession> StartImpersonationSessionAsync(
        User targetUser, Membership targetMembership, Guid impersonatorUserId,
        string? ipAddress, string? userAgent, CancellationToken cancellationToken) =>
        StartSessionAsync(targetUser, targetMembership, impersonatorUserId, resolveDefault: false, mfaPreSatisfied: false, ipAddress, userAgent, cancellationToken);

    private async Task<IssuedSession> StartSessionAsync(
        User user, Membership? membership, Guid? impersonatedByUserId, bool resolveDefault, bool mfaPreSatisfied,
        string? ipAddress, string? userAgent, CancellationToken cancellationToken)
    {
        if (resolveDefault)
        {
            membership = await GetDefaultMembershipAsync(user.Id, cancellationToken);
        }

        var session = UserSession.Start(user.Id, ipAddress, userAgent, clock.UtcNow, impersonatedByUserId);

        // When MFA is enabled, the first access token is "pending": no permissions until the
        // second factor is verified, so it can only call the MFA-verify endpoint. The satisfied
        // state is recorded on the session so a refresh can't upgrade a still-pending session.
        // Impersonation is exempt — the operator's own MFA was already satisfied. SSO can also pre-satisfy MFA
        // when the connection trusts the IdP for the second factor (docs/21 §9).
        var mfaRequired = !mfaPreSatisfied && impersonatedByUserId is null && user.MfaEnabled;
        if (!mfaRequired)
        {
            session.MarkMfaSatisfied();
        }

        dbContext.UserSessions.Add(session);

        var refresh = tokenService.IssueRefreshToken(clock.UtcNow.AddDays(RefreshTokenLifetimeDays));
        var refreshToken = RefreshToken.Issue(session.Id, user.Id, refresh.Hash, Guid.NewGuid(), refresh.ExpiresAtUtc);
        dbContext.RefreshTokens.Add(refreshToken);

        var access = await IssueAccessAsync(user, session.Id, membership, mfaSatisfied: !mfaRequired, cancellationToken, impersonatedByUserId);

        return new IssuedSession(access, refresh.Value, mfaRequired, session.Id, membership?.Id);
    }

    /// <summary>Re-issue an access token for an existing session (after MFA verify or refresh).</summary>
    public async Task<AccessToken> IssueAccessAsync(
        User user, Guid sessionId, Membership? membership, bool mfaSatisfied, CancellationToken cancellationToken,
        Guid? impersonatorUserId = null)
    {
        IReadOnlyCollection<string> permissions = mfaSatisfied && membership is not null
            ? await permissionService.GetEffectivePermissionsAsync(membership.Id, cancellationToken)
            : [];

        var subject = new TokenSubject(
            UserId: user.Id,
            Email: user.Email.Value,
            SessionId: sessionId,
            OrganizationId: membership?.OrganizationId,
            MembershipId: membership?.Id,
            Permissions: permissions,
            MfaSatisfied: mfaSatisfied,
            SecurityStamp: user.SecurityStamp,
            // Impersonation drops platform privileges, so an impersonation token never carries the role.
            PlatformRole: impersonatorUserId is null && user.PlatformRole != WireHQ.Domain.Identity.PlatformRole.None
                ? user.PlatformRole.ToString()
                : null,
            ImpersonatorUserId: impersonatorUserId);

        return tokenService.IssueAccessToken(subject);
    }

    public async Task<Membership?> GetDefaultMembershipAsync(Guid userId, CancellationToken cancellationToken) =>
        await dbContext.Memberships
            .IgnoreQueryFilters()
            .Where(m => m.UserId == userId && m.Status == MembershipStatus.Active && !m.IsDeleted)
            .OrderBy(m => m.JoinedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);
}

public sealed record IssuedSession(
    AccessToken AccessToken,
    string RefreshToken,
    bool MfaRequired,
    Guid SessionId,
    Guid? MembershipId);
