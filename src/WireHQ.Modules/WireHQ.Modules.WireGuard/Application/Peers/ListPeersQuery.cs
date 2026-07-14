using Microsoft.EntityFrameworkCore;
using WireHQ.Application.Abstractions.Persistence;
using WireHQ.Application.Common.Messaging;
using WireHQ.Modules.WireGuard.Authorization;
using WireHQ.Modules.WireGuard.Domain;
using WireHQ.Shared.Results;

namespace WireHQ.Modules.WireGuard.Application.Peers;

public sealed record ListPeersQuery(Guid InstanceId) : IQuery<IReadOnlyList<PeerListItem>>, IAuthorizedRequest
{
    public IReadOnlyCollection<string> RequiredPermissions => [WireGuardPermissions.Peers.Read];
}

public sealed record PeerListItem(
    Guid Id,
    string Name,
    string? Email,
    string? DeviceType,
    string Status,
    string AssignedAddress,
    string PublicKey,
    DateTimeOffset? LastHandshakeAtUtc,
    long RxBytes,
    long TxBytes);

public sealed class ListPeersQueryHandler(IApplicationDbContext dbContext)
    : IQueryHandler<ListPeersQuery, IReadOnlyList<PeerListItem>>
{
    public async Task<Result<IReadOnlyList<PeerListItem>>> Handle(ListPeersQuery query, CancellationToken cancellationToken)
    {
        var rows = await dbContext.Set<Peer>()
            .Where(p => p.InstanceId == query.InstanceId)
            .OrderBy(p => p.Name)
            .Select(p => new
            {
                p.Id, p.Name, p.Email, p.DeviceType, p.Status, p.AssignedAddress, p.PublicKey,
                p.LastHandshakeAtUtc, p.RxBytes, p.TxBytes,
            })
            .ToListAsync(cancellationToken);

        IReadOnlyList<PeerListItem> items = rows
            .Select(p => new PeerListItem(
                p.Id, p.Name, p.Email, p.DeviceType, p.Status.ToString(), p.AssignedAddress, p.PublicKey,
                p.LastHandshakeAtUtc, p.RxBytes, p.TxBytes))
            .ToList();

        return Result.Success(items);
    }
}
