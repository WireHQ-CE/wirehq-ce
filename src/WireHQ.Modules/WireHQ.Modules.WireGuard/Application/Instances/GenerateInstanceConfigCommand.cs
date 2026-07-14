using Microsoft.EntityFrameworkCore;
using WireHQ.Application.Abstractions;
using WireHQ.Application.Abstractions.Persistence;
using WireHQ.Application.Common.Messaging;
using WireHQ.Domain.Auditing;
using WireHQ.Modules.WireGuard.Authorization;
using WireHQ.Modules.WireGuard.Domain;
using WireHQ.Modules.WireGuard.Services;
using WireHQ.Shared.Results;

namespace WireHQ.Modules.WireGuard.Application.Instances;

/// <summary>
/// Exports the full server (instance) <c>wg-quick</c> config: the <c>[Interface]</c> plus one
/// <c>[Peer]</c> block per <b>active</b> peer (disabled/revoked peers are excluded so they can't
/// connect). Reveals the server private key + each peer's preshared key, so it is a command (audited),
/// mirroring the peer config export. This is the artifact you deploy on the WireGuard server.
/// </summary>
public sealed record GenerateInstanceConfigCommand(Guid InstanceId) : ICommand<InstanceConfigResult>, IAuthorizedRequest
{
    public IReadOnlyCollection<string> RequiredPermissions => [WireGuardPermissions.Instances.Export];
}

public sealed record InstanceConfigResult(string Config, string FileName, int PeerCount);

public sealed class GenerateInstanceConfigCommandHandler(
    IApplicationDbContext dbContext,
    IKeyManagementService keys,
    IConfigurationService configuration,
    IAuditWriter audit)
    : ICommandHandler<GenerateInstanceConfigCommand, InstanceConfigResult>
{
    public async Task<Result<InstanceConfigResult>> Handle(GenerateInstanceConfigCommand command, CancellationToken cancellationToken)
    {
        var instance = await dbContext.Set<WireGuardInstance>()
            .FirstOrDefaultAsync(i => i.Id == command.InstanceId, cancellationToken);
        if (instance is null)
        {
            return WireGuardErrors.Instance.NotFound;
        }

        // AgentManaged instances hold no server private key in WireHQ, so the full server config (which
        // includes the [Interface] PrivateKey) cannot be exported. Per-peer/client exports still work.
        if (instance.PrivateKeyId is not { } privateKeyId)
        {
            return WireGuardErrors.Key.ServerKeyAgentManaged;
        }

        var privateKey = await keys.RevealAsync(privateKeyId, cancellationToken);
        if (privateKey is null)
        {
            return WireGuardErrors.Key.NotFound;
        }

        // Only Active peers belong in the server config — a disabled/revoked peer must not be able to connect.
        var peers = await dbContext.Set<Peer>()
            .Where(p => p.InstanceId == instance.Id && p.Status == PeerStatus.Active)
            .OrderBy(p => p.AssignedAddress)
            .ToListAsync(cancellationToken);

        var entries = new List<InstancePeerEntry>(peers.Count);
        foreach (var peer in peers)
        {
            var presharedKey = peer.PresharedKeyId is { } pskId ? await keys.RevealAsync(pskId, cancellationToken) : null;
            entries.Add(new InstancePeerEntry(peer.PublicKey, presharedKey, peer.AssignedAddress));
        }

        var config = configuration.RenderInstanceConfig(new InstanceConfigInput(
            privateKey, instance.InterfaceAddress, instance.ListenPort, instance.Mtu, entries));

        audit.Record("wg.instance.config_exported", AuditOutcome.Success, nameof(WireGuardInstance), instance.Id.ToString(),
            new { peerCount = entries.Count });

        return new InstanceConfigResult(config, $"{instance.Slug.Value}.conf", entries.Count);
    }
}
