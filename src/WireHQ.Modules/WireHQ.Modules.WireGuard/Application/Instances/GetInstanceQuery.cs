using Microsoft.EntityFrameworkCore;
using WireHQ.Application.Abstractions.Persistence;
using WireHQ.Application.Common.Messaging;
using WireHQ.Modules.WireGuard.Authorization;
using WireHQ.Modules.WireGuard.Domain;
using WireHQ.Modules.WireGuard.Providers;
using WireHQ.Shared.Results;

namespace WireHQ.Modules.WireGuard.Application.Instances;

public sealed record GetInstanceQuery(Guid Id) : IQuery<InstanceDetail>, IAuthorizedRequest
{
    public IReadOnlyCollection<string> RequiredPermissions => [WireGuardPermissions.Instances.Read];
}

public sealed record InstanceDetail(
    Guid Id,
    string Name,
    string Slug,
    string? Description,
    Guid NetworkId,
    string ProviderType,
    int ListenPort,
    string InterfaceAddress,
    string PublicKey,
    IReadOnlyCollection<string> Dns,
    string? EndpointHost,
    int Mtu,
    string Status,
    int PeerCount,
    bool CanControl,
    bool HasLiveStatus,
    DateTimeOffset CreatedAtUtc);

public sealed class GetInstanceQueryHandler(IApplicationDbContext dbContext, IWireGuardProviderFactory providerFactory)
    : IQueryHandler<GetInstanceQuery, InstanceDetail>
{
    public async Task<Result<InstanceDetail>> Handle(GetInstanceQuery query, CancellationToken cancellationToken)
    {
        var instance = await dbContext.Set<WireGuardInstance>()
            .FirstOrDefaultAsync(i => i.Id == query.Id, cancellationToken);

        if (instance is null)
        {
            return WireGuardErrors.Instance.NotFound;
        }

        var peerCount = await dbContext.Set<Peer>().CountAsync(p => p.InstanceId == instance.Id, cancellationToken);

        // The UI degrades against provider capabilities (hide start/stop + telemetry for config-only).
        var capabilities = providerFactory.Resolve(instance.ProviderType).Capabilities;

        return new InstanceDetail(
            instance.Id, instance.Name, instance.Slug.Value, instance.Description, instance.NetworkId,
            instance.ProviderType.ToString(), instance.ListenPort, instance.InterfaceAddress, instance.PublicKey,
            instance.Dns, instance.EndpointHost, instance.Mtu, instance.Status.ToString(), peerCount,
            capabilities.HasFlag(ProviderCapabilities.ControlInterface),
            capabilities.HasFlag(ProviderCapabilities.LiveStatus),
            instance.CreatedAtUtc);
    }
}
