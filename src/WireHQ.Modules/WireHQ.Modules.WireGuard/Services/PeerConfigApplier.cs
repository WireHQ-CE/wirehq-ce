using Microsoft.EntityFrameworkCore;
using WireHQ.Application.Abstractions.Persistence;
using WireHQ.Modules.WireGuard.Domain;
using WireHQ.Modules.WireGuard.Providers;
using WireHQ.Shared.Results;

namespace WireHQ.Modules.WireGuard.Services;

/// <summary>
/// Renders + versions a peer's config (when WireHQ holds its key) and applies its current routing spec to the
/// instance's provider — the shared "a peer's routing changed, deploy it" step. Extracted from
/// <c>UpdatePeerCommand</c> so the Access Policies apply path (wave 2) reuses the <b>exact</b> deploy behaviour
/// rather than reimplementing it. The peer's routing fields must already be set by the caller; this runs inside
/// the caller's unit of work (it mutates nothing itself beyond writing a config version + the provider call).
/// No-op for an agent-rendered peer (no WireHQ-held key). Core.
/// </summary>
public interface IPeerConfigApplier
{
    Task<Result> ApplyAsync(Peer peer, string reason, CancellationToken cancellationToken);
}

public sealed class PeerConfigApplier(
    IApplicationDbContext dbContext,
    IKeyManagementService keys,
    IConfigurationService configuration,
    IConfigVersionWriter configVersions,
    IWireGuardProviderFactory providerFactory)
    : IPeerConfigApplier
{
    public async Task<Result> ApplyAsync(Peer peer, string reason, CancellationToken cancellationToken)
    {
        // Nothing WireHQ can render/apply for a peer whose key it doesn't hold (agent-managed); the config
        // reaches the peer via the instance deploy instead. Matches UpdatePeerCommand's gate.
        if (peer.PrivateKeyId is not { } privateKeyId)
        {
            return Result.Success();
        }

        var instance = await dbContext.Set<WireGuardInstance>().FirstOrDefaultAsync(i => i.Id == peer.InstanceId, cancellationToken);
        if (instance is null)
        {
            return WireGuardErrors.Instance.NotFound;
        }

        var network = await dbContext.Set<WireGuardNetwork>().FirstOrDefaultAsync(n => n.Id == instance.NetworkId, cancellationToken);
        var privateKey = await keys.RevealAsync(privateKeyId, cancellationToken);
        var presharedKey = peer.PresharedKeyId is { } pskId ? await keys.RevealAsync(pskId, cancellationToken) : null;

        if (privateKey is not null)
        {
            var config = configuration.RenderPeerConfig(new PeerConfigInput(
                privateKey, peer.AssignedAddress, network?.Dns.ToList() ?? [], instance.Mtu,
                instance.PublicKey, presharedKey, instance.EndpointHost, peer.AllowedIps.ToList(), peer.PersistentKeepalive));
            await configVersions.WriteAsync(ConfigTargetType.Peer, peer.Id, config, reason, cancellationToken);
        }

        var provider = providerFactory.Resolve(instance.ProviderType);
        var providerRef = new ProviderInstanceRef(instance.Id, instance.ExternalId,
            instance.ProviderSettings.ToDictionary(kv => kv.Key, kv => kv.Value));
        await provider.ApplyPeerAsync(providerRef,
            new ProviderPeerSpec(peer.PublicKey, presharedKey, peer.AllowedIps.ToList(), null, peer.PersistentKeepalive), cancellationToken);

        return Result.Success();
    }
}
