using Microsoft.EntityFrameworkCore;
using WireHQ.Application.Abstractions.Persistence;
using WireHQ.Application.Common.Messaging;
using WireHQ.Modules.WireGuard.Authorization;
using WireHQ.Modules.WireGuard.Domain;
using WireHQ.Shared.Results;

namespace WireHQ.Modules.WireGuard.Application.Networks;

public sealed record ListNetworksQuery : IQuery<IReadOnlyList<NetworkListItem>>, IAuthorizedRequest
{
    public IReadOnlyCollection<string> RequiredPermissions => [WireGuardPermissions.Instances.Read];
}

public sealed record NetworkListItem(Guid Id, string Name, string Cidr, int InstanceCount);

public sealed class ListNetworksQueryHandler(IApplicationDbContext dbContext)
    : IQueryHandler<ListNetworksQuery, IReadOnlyList<NetworkListItem>>
{
    public async Task<Result<IReadOnlyList<NetworkListItem>>> Handle(ListNetworksQuery query, CancellationToken cancellationToken)
    {
        IReadOnlyList<NetworkListItem> items = await dbContext.Set<WireGuardNetwork>()
            .OrderBy(n => n.Name)
            .Select(n => new NetworkListItem(
                n.Id, n.Name, n.Cidr,
                dbContext.Set<WireGuardInstance>().Count(i => i.NetworkId == n.Id)))
            .ToListAsync(cancellationToken);

        return Result.Success(items);
    }
}
