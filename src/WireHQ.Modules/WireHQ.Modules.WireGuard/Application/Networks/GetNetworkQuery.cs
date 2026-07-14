using Microsoft.EntityFrameworkCore;
using WireHQ.Application.Abstractions.Persistence;
using WireHQ.Application.Common.Messaging;
using WireHQ.Modules.WireGuard.Authorization;
using WireHQ.Modules.WireGuard.Domain;
using WireHQ.Shared.Results;

namespace WireHQ.Modules.WireGuard.Application.Networks;

public sealed record GetNetworkQuery(Guid Id) : IQuery<NetworkDetail>, IAuthorizedRequest
{
    public IReadOnlyCollection<string> RequiredPermissions => [WireGuardPermissions.Instances.Read];
}

public sealed record NetworkDetail(
    Guid Id,
    string Name,
    string Cidr,
    IReadOnlyCollection<string> Dns,
    IReadOnlyCollection<string> DefaultAllowedIps,
    int InstanceCount,
    DateTimeOffset CreatedAtUtc);

public sealed class GetNetworkQueryHandler(IApplicationDbContext dbContext)
    : IQueryHandler<GetNetworkQuery, NetworkDetail>
{
    public async Task<Result<NetworkDetail>> Handle(GetNetworkQuery query, CancellationToken cancellationToken)
    {
        var network = await dbContext.Set<WireGuardNetwork>().FirstOrDefaultAsync(n => n.Id == query.Id, cancellationToken);
        if (network is null)
        {
            return WireGuardErrors.Network.NotFound;
        }

        var instanceCount = await dbContext.Set<WireGuardInstance>().CountAsync(i => i.NetworkId == network.Id, cancellationToken);

        return new NetworkDetail(
            network.Id, network.Name, network.Cidr, network.Dns, network.DefaultAllowedIps, instanceCount, network.CreatedAtUtc);
    }
}
