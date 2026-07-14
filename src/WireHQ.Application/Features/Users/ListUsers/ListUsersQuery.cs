using Microsoft.EntityFrameworkCore;
using WireHQ.Application.Abstractions;
using WireHQ.Application.Authorization;
using WireHQ.Application.Abstractions.Persistence;
using WireHQ.Application.Common.Messaging;
using WireHQ.Shared.Primitives;
using WireHQ.Shared.Results;

namespace WireHQ.Application.Features.Users.ListUsers;

/// <summary>
/// Lists members of the active organization. Memberships are scoped to the active org explicitly
/// because the join reads the global users table with <c>IgnoreQueryFilters()</c>, which is
/// query-wide — it would otherwise also disable the Memberships tenant filter. (ADR-024 pattern.)
/// </summary>
public sealed record ListUsersQuery(string? Search, int Page = 1, int PageSize = 25)
    : IQuery<PagedList<UserListItem>>, IAuthorizedRequest
{
    public IReadOnlyCollection<string> RequiredPermissions => [Permissions.Users.Read];
}

public sealed record UserListItem(
    Guid UserId,
    Guid MembershipId,
    string Email,
    string Name,
    string Status,
    DateTimeOffset? JoinedAtUtc);

public sealed class ListUsersQueryHandler(IApplicationDbContext dbContext, ITenantContext tenant)
    : IQueryHandler<ListUsersQuery, PagedList<UserListItem>>
{
    public async Task<Result<PagedList<UserListItem>>> Handle(ListUsersQuery query, CancellationToken cancellationToken)
    {
        var paging = new PaginationRequest(query.Page, query.PageSize);

        // Scope to the active org EXPLICITLY: the join's IgnoreQueryFilters() (needed to read the
        // global users table) is query-wide and would otherwise disable the Memberships tenant
        // filter, leaking other orgs' members. A null org (no tenant) yields no rows.
        var orgId = tenant.OrganizationId;
        var rows = dbContext.Memberships
            .Where(m => !m.IsDeleted && m.OrganizationId == orgId)
            .Join(
                dbContext.Users.IgnoreQueryFilters(),
                m => m.UserId,
                u => u.Id,
                (m, u) => new { m, u });

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim().ToLower();
            rows = rows.Where(x => x.u.Name.ToLower().Contains(term) || x.u.Email.Value.Contains(term));
        }

        var total = await rows.CountAsync(cancellationToken);

        var items = await rows
            .OrderBy(x => x.u.Name)
            .Skip(paging.Skip)
            .Take(paging.PageSize)
            .Select(x => new UserListItem(
                x.u.Id,
                x.m.Id,
                x.u.Email.Value,
                x.u.Name,
                x.m.Status.ToString(),
                x.m.JoinedAtUtc))
            .ToListAsync(cancellationToken);

        return new PagedList<UserListItem>(items, paging.Page, paging.PageSize, total);
    }
}
