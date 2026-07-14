using Microsoft.EntityFrameworkCore;
using WireHQ.Application.Abstractions;
using WireHQ.Application.Abstractions.Persistence;
using WireHQ.Application.Abstractions.Security;
using WireHQ.Application.Common.Messaging;
using WireHQ.Shared.Results;

namespace WireHQ.Application.Features.Authentication.Refresh;

public sealed record RefreshTokenCommand(string RefreshToken) : ICommand<RefreshTokenResponse>, ITenantUnscopedRequest;

public sealed record RefreshTokenResponse(string AccessToken, int ExpiresIn, string RefreshToken);

public static class RefreshErrors
{
    public static readonly Error Invalid = Error.Unauthorized("auth.invalid_refresh_token", "Invalid or expired session. Please sign in again.");
}

/// <summary>
/// Rotates a refresh token: validates it, issues a successor in the same family, and returns a
/// new access token. Presenting an already-rotated/revoked token is treated as theft — the
/// whole family is revoked (reuse detection). (docs/04-security.md)
/// </summary>
public sealed class RefreshTokenCommandHandler(
    IApplicationDbContext dbContext,
    ITokenService tokenService,
    AuthSessionService sessions,
    IDateTimeProvider clock)
    : ICommandHandler<RefreshTokenCommand, RefreshTokenResponse>
{
    public async Task<Result<RefreshTokenResponse>> Handle(RefreshTokenCommand command, CancellationToken cancellationToken)
    {
        var hash = tokenService.HashRefreshToken(command.RefreshToken);

        var stored = await dbContext.RefreshTokens
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.TokenHash == hash, cancellationToken);

        if (stored is null)
        {
            return RefreshErrors.Invalid;
        }

        // Reuse detection: a token that has already been rotated or revoked must never be
        // accepted again. Its presentation means the chain leaked — burn the whole family.
        if (!stored.IsActive(clock.UtcNow))
        {
            await RevokeFamilyAsync(stored.FamilyId, cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);
            return RefreshErrors.Invalid;
        }

        var session = await dbContext.UserSessions
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.Id == stored.SessionId, cancellationToken);

        if (session is null || !session.IsActive)
        {
            return RefreshErrors.Invalid;
        }

        // Time-box impersonation (ADR-032): an impersonation session past its hard cap cannot be renewed —
        // revoke it and refuse, so "act as customer" ends within one access-token lifetime of the window
        // rather than living for the full refresh-token horizon. Re-entry requires a fresh, audited start.
        if (session.IsImpersonation
            && clock.UtcNow >= session.CreatedAtUtc.AddMinutes(AuthSessionService.ImpersonationSessionLifetimeMinutes))
        {
            session.Revoke(clock.UtcNow);
            await dbContext.SaveChangesAsync(cancellationToken);
            return RefreshErrors.Invalid;
        }

        var user = await dbContext.Users
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.Id == stored.UserId && !u.IsDeleted, cancellationToken);

        if (user is null)
        {
            return RefreshErrors.Invalid;
        }

        var raw = tokenService.IssueRefreshToken(clock.UtcNow.AddDays(AuthSessionService.RefreshTokenLifetimeDays));
        var next = stored.Rotate(raw.Hash, clock.UtcNow, raw.ExpiresAtUtc);
        dbContext.RefreshTokens.Add(next);

        session.Touch(clock.UtcNow);

        var membership = await sessions.GetDefaultMembershipAsync(user.Id, cancellationToken);
        // Honor the session's MFA state: a session that hasn't completed MFA stays "pending" on refresh.
        // Preserve impersonation across refresh so the successor token keeps the impersonator claim.
        var access = await sessions.IssueAccessAsync(
            user, session.Id, membership, session.MfaSatisfied, cancellationToken, session.ImpersonatedByUserId);

        return new RefreshTokenResponse(access.Value, access.ExpiresInSeconds, raw.Value);
    }

    private async Task RevokeFamilyAsync(Guid familyId, CancellationToken cancellationToken)
    {
        var family = await dbContext.RefreshTokens
            .IgnoreQueryFilters()
            .Where(t => t.FamilyId == familyId && t.RevokedAtUtc == null)
            .ToListAsync(cancellationToken);

        foreach (var token in family)
        {
            token.Revoke(clock.UtcNow);
        }
    }
}
