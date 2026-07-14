using Microsoft.EntityFrameworkCore;
using WireHQ.Application.Abstractions.Persistence;
using WireHQ.Application.Common.Messaging;
using WireHQ.Modules.WireGuard.Authorization;
using WireHQ.Modules.WireGuard.Domain;
using WireHQ.Shared.Results;

namespace WireHQ.Modules.WireGuard.Application.Instances;

public sealed record ListInstancesQuery : IQuery<IReadOnlyList<InstanceListItem>>, IAuthorizedRequest
{
    public IReadOnlyCollection<string> RequiredPermissions => [WireGuardPermissions.Instances.Read];
}

public sealed record InstanceListItem(
    Guid Id,
    string Name,
    string Slug,
    string ProviderType,
    int ListenPort,
    string InterfaceAddress,
    string Status,
    int PeerCount,
    DateTimeOffset CreatedAtUtc);

public sealed class ListInstancesQueryHandler(IApplicationDbContext dbContext)
    : IQueryHandler<ListInstancesQuery, IReadOnlyList<InstanceListItem>>
{
    public async Task<Result<IReadOnlyList<InstanceListItem>>> Handle(ListInstancesQuery query, CancellationToken cancellationToken)
    {
        // Tenant scoping is automatic (WireGuardInstance/Peer are ITenantOwned → global filter).
        var rows = await dbContext.Set<WireGuardInstance>()
            .OrderBy(i => i.Name)
            .Select(i => new
            {
                i.Id,
                i.Name,
                Slug = i.Slug.Value,
                i.ProviderType,
                i.ListenPort,
                i.InterfaceAddress,
                i.Status,
                PeerCount = dbContext.Set<Peer>().Count(p => p.InstanceId == i.Id),
                i.CreatedAtUtc,
            })
            .ToListAsync(cancellationToken);

        IReadOnlyList<InstanceListItem> items = rows
            .Select(r => new InstanceListItem(
                r.Id, r.Name, r.Slug, r.ProviderType.ToString(), r.ListenPort, r.InterfaceAddress,
                r.Status.ToString(), r.PeerCount, r.CreatedAtUtc))
            .ToList();

        return Result.Success(items);
    }
}
