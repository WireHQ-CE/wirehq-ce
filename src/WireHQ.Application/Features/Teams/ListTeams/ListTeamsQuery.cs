using Microsoft.EntityFrameworkCore;
using WireHQ.Application.Abstractions.Persistence;
using WireHQ.Application.Authorization;
using WireHQ.Application.Common.Messaging;
using WireHQ.Shared.Results;

namespace WireHQ.Application.Features.Teams.ListTeams;

/// <summary>
/// Lists the teams in the active organization. Tenant scoping is automatic — the Teams query
/// filter restricts rows to the current org, so this handler never writes a tenant predicate.
/// </summary>
public sealed record ListTeamsQuery(string? Search = null)
    : IQuery<IReadOnlyList<TeamListItem>>, IAuthorizedRequest
{
    public IReadOnlyCollection<string> RequiredPermissions => [Permissions.Teams.Read];
}

public sealed record TeamListItem(
    Guid Id,
    string Name,
    string Slug,
    string? Description,
    int MemberCount,
    DateTimeOffset CreatedAtUtc);

public sealed class ListTeamsQueryHandler(IApplicationDbContext dbContext)
    : IQueryHandler<ListTeamsQuery, IReadOnlyList<TeamListItem>>
{
    public async Task<Result<IReadOnlyList<TeamListItem>>> Handle(ListTeamsQuery query, CancellationToken cancellationToken)
    {
        var teams = dbContext.Teams.AsQueryable();

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim().ToLower();
            teams = teams.Where(t => t.Name.ToLower().Contains(term));
        }

        var items = await teams
            .OrderBy(t => t.Name)
            .Select(t => new TeamListItem(
                t.Id,
                t.Name,
                t.Slug.Value,
                t.Description,
                t.Members.Count,
                t.CreatedAtUtc))
            .ToListAsync(cancellationToken);

        return items;
    }
}
