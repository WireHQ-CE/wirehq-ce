using Microsoft.EntityFrameworkCore;
using WireHQ.Application.Abstractions;
using WireHQ.Application.Abstractions.Persistence;
using WireHQ.Application.Common.Messaging;
using WireHQ.Modules.WireGuard.Authorization;
using WireHQ.Modules.WireGuard.Domain;
using WireHQ.Shared.Results;

namespace WireHQ.Modules.WireGuard.Application.Dashboard;

public sealed record GetWireGuardOverviewQuery : IQuery<WireGuardOverview>, IAuthorizedRequest
{
    public IReadOnlyCollection<string> RequiredPermissions => [WireGuardPermissions.Instances.Read];
}

public sealed record WireGuardOverview(
    int Instances,
    int Networks,
    int Peers,
    int ActivePeers,
    int RecentHandshakes,
    long TotalRxBytes,
    long TotalTxBytes);

public sealed class GetWireGuardOverviewQueryHandler(IApplicationDbContext dbContext, IDateTimeProvider clock)
    : IQueryHandler<GetWireGuardOverviewQuery, WireGuardOverview>
{
    public async Task<Result<WireGuardOverview>> Handle(GetWireGuardOverviewQuery query, CancellationToken cancellationToken)
    {
        var recentCutoff = clock.UtcNow.AddMinutes(-5);
        var peers = dbContext.Set<Peer>();

        var overview = new WireGuardOverview(
            Instances: await dbContext.Set<WireGuardInstance>().CountAsync(cancellationToken),
            Networks: await dbContext.Set<WireGuardNetwork>().CountAsync(cancellationToken),
            Peers: await peers.CountAsync(cancellationToken),
            ActivePeers: await peers.CountAsync(p => p.Status == PeerStatus.Active, cancellationToken),
            RecentHandshakes: await peers.CountAsync(p => p.LastHandshakeAtUtc != null && p.LastHandshakeAtUtc > recentCutoff, cancellationToken),
            // (long?) so SUM over zero peers yields NULL → coalesced to 0 instead of throwing.
            TotalRxBytes: await peers.SumAsync(p => (long?)p.RxBytes, cancellationToken) ?? 0,
            TotalTxBytes: await peers.SumAsync(p => (long?)p.TxBytes, cancellationToken) ?? 0);

        return Result.Success(overview);
    }
}
