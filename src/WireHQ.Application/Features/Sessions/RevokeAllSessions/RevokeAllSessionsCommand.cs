using Microsoft.EntityFrameworkCore;
using WireHQ.Application.Abstractions;
using WireHQ.Application.Abstractions.Persistence;
using WireHQ.Application.Common.Messaging;
using WireHQ.Domain.Auditing;
using WireHQ.Shared.Results;

namespace WireHQ.Application.Features.Sessions.RevokeAllSessions;

/// <summary>"Log out everywhere": revokes all of the user's sessions except the current one.</summary>
public sealed record RevokeAllSessionsCommand : ICommand;

public sealed class RevokeAllSessionsCommandHandler(
    IApplicationDbContext dbContext,
    ICurrentUser currentUser,
    IDateTimeProvider clock,
    IAuditWriter audit)
    : ICommandHandler<RevokeAllSessionsCommand>
{
    private static readonly Error NotAuthenticated = Error.Unauthorized("auth.unauthenticated", "Authentication is required.");

    public async Task<Result> Handle(RevokeAllSessionsCommand command, CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId)
        {
            return NotAuthenticated;
        }

        var currentSessionId = currentUser.SessionId;

        var sessions = await dbContext.UserSessions
            .IgnoreQueryFilters()
            .Where(s => s.UserId == userId && s.RevokedAtUtc == null && s.Id != currentSessionId)
            .ToListAsync(cancellationToken);
        foreach (var session in sessions)
        {
            session.Revoke(clock.UtcNow);
        }

        var tokens = await dbContext.RefreshTokens
            .IgnoreQueryFilters()
            .Where(t => t.UserId == userId && t.RevokedAtUtc == null && t.SessionId != currentSessionId)
            .ToListAsync(cancellationToken);
        foreach (var token in tokens)
        {
            token.Revoke(clock.UtcNow);
        }

        audit.Record("account.sessions_revoked_all", AuditOutcome.Success, nameof(Domain.Identity.User), userId.ToString());
        return Result.Success();
    }
}
