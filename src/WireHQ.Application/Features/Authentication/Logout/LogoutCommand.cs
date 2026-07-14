using Microsoft.EntityFrameworkCore;
using WireHQ.Application.Abstractions;
using WireHQ.Application.Abstractions.Persistence;
using WireHQ.Application.Common.Messaging;
using WireHQ.Domain.Auditing;
using WireHQ.Shared.Results;

namespace WireHQ.Application.Features.Authentication.Logout;

public sealed record LogoutCommand : ICommand;

/// <summary>Revokes the current session and all its refresh tokens — an immediate, server-side logout.</summary>
public sealed class LogoutCommandHandler(
    IApplicationDbContext dbContext,
    ICurrentUser currentUser,
    IDateTimeProvider clock,
    IAuditWriter audit)
    : ICommandHandler<LogoutCommand>
{
    public async Task<Result> Handle(LogoutCommand command, CancellationToken cancellationToken)
    {
        if (currentUser.SessionId is not { } sessionId)
        {
            return Result.Success(); // already effectively logged out
        }

        var session = await dbContext.UserSessions
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.Id == sessionId, cancellationToken);

        session?.Revoke(clock.UtcNow);

        var tokens = await dbContext.RefreshTokens
            .IgnoreQueryFilters()
            .Where(t => t.SessionId == sessionId && t.RevokedAtUtc == null)
            .ToListAsync(cancellationToken);

        foreach (var token in tokens)
        {
            token.Revoke(clock.UtcNow);
        }

        audit.Record("auth.logout", AuditOutcome.Success);
        return Result.Success();
    }
}
