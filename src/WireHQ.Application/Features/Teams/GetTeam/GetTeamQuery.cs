using Microsoft.EntityFrameworkCore;
using WireHQ.Application.Abstractions.Persistence;
using WireHQ.Application.Authorization;
using WireHQ.Application.Common.Messaging;
using WireHQ.Domain.Teams;
using WireHQ.Shared.Results;

namespace WireHQ.Application.Features.Teams.GetTeam;

/// <summary>Returns one team (tenant-scoped) with its members resolved to user identities.</summary>
public sealed record GetTeamQuery(Guid Id) : IQuery<TeamDetail>, IAuthorizedRequest
{
    public IReadOnlyCollection<string> RequiredPermissions => [Permissions.Teams.Read];
}

public sealed record TeamDetail(
    Guid Id,
    string Name,
    string Slug,
    string? Description,
    DateTimeOffset CreatedAtUtc,
    IReadOnlyList<TeamMemberItem> Members);

public sealed record TeamMemberItem(
    Guid MembershipId,
    Guid UserId,
    string Name,
    string Email,
    string Status,
    DateTimeOffset AddedAtUtc);

public sealed class GetTeamQueryHandler(IApplicationDbContext dbContext)
    : IQueryHandler<GetTeamQuery, TeamDetail>
{
    public async Task<Result<TeamDetail>> Handle(GetTeamQuery query, CancellationToken cancellationToken)
    {
        // Tenant filter scopes this to the active org.
        var team = await dbContext.Teams
            .Include(t => t.Members)
            .FirstOrDefaultAsync(t => t.Id == query.Id, cancellationToken);
        if (team is null)
        {
            return TeamErrors.NotFound;
        }

        var addedAt = team.Members.ToDictionary(m => m.MembershipId, m => m.AddedAtUtc);
        var membershipIds = addedAt.Keys.ToList();

        // Resolve each membership to its user identity. Memberships are tenant-filtered (same org);
        // the users table is platform-global, so it is read with the filter ignored.
        var rows = await dbContext.Memberships
            .Where(m => membershipIds.Contains(m.Id) && !m.IsDeleted)
            .Join(dbContext.Users.IgnoreQueryFilters(), m => m.UserId, u => u.Id, (m, u) => new
            {
                MembershipId = m.Id,
                UserId = u.Id,
                u.Name,
                Email = u.Email.Value,
                Status = m.Status.ToString(),
            })
            .ToListAsync(cancellationToken);

        var members = rows
            .Select(x => new TeamMemberItem(
                x.MembershipId, x.UserId, x.Name, x.Email, x.Status,
                addedAt.GetValueOrDefault(x.MembershipId)))
            .OrderBy(m => m.Name)
            .ToList();

        return new TeamDetail(team.Id, team.Name, team.Slug.Value, team.Description, team.CreatedAtUtc, members);
    }
}
