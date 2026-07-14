using Microsoft.EntityFrameworkCore;
using WireHQ.Application.Abstractions;
using WireHQ.Application.Abstractions.Persistence;
using WireHQ.Application.Common.Messaging;
using WireHQ.Shared.Results;

namespace WireHQ.Application.Features.Sessions.ListSessions;

/// <summary>Lists the signed-in user's active sessions, flagging the current one.</summary>
public sealed record ListSessionsQuery : IQuery<IReadOnlyList<SessionItem>>;

public sealed record SessionItem(
    Guid Id,
    string? IpAddress,
    string? UserAgent,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset LastSeenAtUtc,
    bool IsCurrent);

public sealed class ListSessionsQueryHandler(IApplicationDbContext dbContext, ICurrentUser currentUser)
    : IQueryHandler<ListSessionsQuery, IReadOnlyList<SessionItem>>
{
    private static readonly Error NotAuthenticated = Error.Unauthorized("auth.unauthenticated", "Authentication is required.");

    public async Task<Result<IReadOnlyList<SessionItem>>> Handle(ListSessionsQuery query, CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId)
        {
            return NotAuthenticated;
        }

        var currentSessionId = currentUser.SessionId;

        var sessions = await dbContext.UserSessions
            .IgnoreQueryFilters()
            .Where(s => s.UserId == userId && s.RevokedAtUtc == null)
            .OrderByDescending(s => s.LastSeenAtUtc)
            .Select(s => new SessionItem(
                s.Id,
                s.IpAddress,
                s.UserAgent,
                s.CreatedAtUtc,
                s.LastSeenAtUtc,
                s.Id == currentSessionId))
            .ToListAsync(cancellationToken);

        return sessions;
    }
}
