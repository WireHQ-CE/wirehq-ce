using Microsoft.EntityFrameworkCore;
using WireHQ.Application.Abstractions;
using WireHQ.Application.Abstractions.Persistence;
using WireHQ.Application.Common.Messaging;
using WireHQ.Domain.Auditing;
using WireHQ.Shared.Results;

namespace WireHQ.Application.Features.Sessions.RevokeSession;

/// <summary>Revokes one of the current user's sessions (and its refresh tokens) — an immediate sign-out.</summary>
public sealed record RevokeSessionCommand(Guid SessionId) : ICommand;

public sealed class RevokeSessionCommandHandler(
    IApplicationDbContext dbContext,
    ICurrentUser currentUser,
    IDateTimeProvider clock,
    IAuditWriter audit)
    : ICommandHandler<RevokeSessionCommand>
{
    private static readonly Error NotAuthenticated = Error.Unauthorized("auth.unauthenticated", "Authentication is required.");

    public async Task<Result> Handle(RevokeSessionCommand command, CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId)
        {
            return NotAuthenticated;
        }

        // Ownership check: only the user's own sessions can be revoked here.
        var session = await dbContext.UserSessions
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.Id == command.SessionId && s.UserId == userId, cancellationToken);

        if (session is null)
        {
            return Result.Success(); // idempotent
        }

        session.Revoke(clock.UtcNow);

        var tokens = await dbContext.RefreshTokens
            .IgnoreQueryFilters()
            .Where(t => t.SessionId == session.Id && t.RevokedAtUtc == null)
            .ToListAsync(cancellationToken);
        foreach (var token in tokens)
        {
            token.Revoke(clock.UtcNow);
        }

        audit.Record("account.session_revoked", AuditOutcome.Success, "UserSession", session.Id.ToString());
        return Result.Success();
    }
}
