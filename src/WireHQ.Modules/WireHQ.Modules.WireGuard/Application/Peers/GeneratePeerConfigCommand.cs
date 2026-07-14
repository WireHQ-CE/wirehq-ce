using Microsoft.EntityFrameworkCore;
using WireHQ.Application.Abstractions.Persistence;
using WireHQ.Application.Common.Messaging;
using WireHQ.Domain.Auditing;
using WireHQ.Application.Abstractions;
using WireHQ.Modules.WireGuard.Authorization;
using WireHQ.Modules.WireGuard.Domain;
using WireHQ.Modules.WireGuard.Services;
using WireHQ.Shared.Results;

namespace WireHQ.Modules.WireGuard.Application.Peers;

/// <summary>
/// Exports a peer's client config (reveals the private key once). A command (not a query) because
/// revealing key material is a security-sensitive action that must be audited. (docs/11 §5/§6)
/// </summary>
public sealed record GeneratePeerConfigCommand(Guid PeerId) : ICommand<PeerConfigResult>, IAuthorizedRequest
{
    public IReadOnlyCollection<string> RequiredPermissions => [WireGuardPermissions.Peers.Export];
}

public sealed record PeerConfigResult(string Config, string FileName);

public sealed class GeneratePeerConfigCommandHandler(
    IApplicationDbContext dbContext,
    IKeyManagementService keys,
    IConfigurationService configuration,
    IAuditWriter audit)
    : ICommandHandler<GeneratePeerConfigCommand, PeerConfigResult>
{
    public async Task<Result<PeerConfigResult>> Handle(GeneratePeerConfigCommand command, CancellationToken cancellationToken)
    {
        var peer = await dbContext.Set<Peer>().FirstOrDefaultAsync(p => p.Id == command.PeerId, cancellationToken);
        if (peer is null)
        {
            return WireGuardErrors.Peer.NotFound;
        }

        if (peer.PrivateKeyId is not { } privateKeyId)
        {
            return WireGuardErrors.Key.PrivateKeyUnavailable;
        }

        var instance = await dbContext.Set<WireGuardInstance>().FirstOrDefaultAsync(i => i.Id == peer.InstanceId, cancellationToken);
        if (instance is null)
        {
            return WireGuardErrors.Instance.NotFound;
        }

        var network = await dbContext.Set<WireGuardNetwork>().FirstOrDefaultAsync(n => n.Id == instance.NetworkId, cancellationToken);

        var privateKey = await keys.RevealAsync(privateKeyId, cancellationToken);
        if (privateKey is null)
        {
            return WireGuardErrors.Key.NotFound;
        }

        var presharedKey = peer.PresharedKeyId is { } pskId ? await keys.RevealAsync(pskId, cancellationToken) : null;

        var config = configuration.RenderPeerConfig(new PeerConfigInput(
            privateKey, peer.AssignedAddress, network?.Dns.ToList() ?? [], instance.Mtu,
            instance.PublicKey, presharedKey, instance.EndpointHost, peer.AllowedIps.ToList(), peer.PersistentKeepalive));

        audit.Record("wg.peer.config_exported", AuditOutcome.Success, nameof(Peer), peer.Id.ToString());

        var fileName = $"{instance.Slug.Value}-{peer.Id.ToString()[..8]}.conf";
        return new PeerConfigResult(config, fileName);
    }
}
