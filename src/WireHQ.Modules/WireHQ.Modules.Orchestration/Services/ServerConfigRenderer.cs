using Microsoft.EntityFrameworkCore;
using WireHQ.Application.Abstractions.Persistence;
using WireHQ.Modules.Orchestration.Domain;
using WireHQ.Modules.WireGuard.Domain;
using WireHQ.Modules.WireGuard.Providers;
using WireHQ.Modules.WireGuard.Services;
using WireHQ.Shared.Results;

namespace WireHQ.Modules.Orchestration.Services;

/// <summary>Renders an instance's full desired server config (interface + a [Peer] per active peer).</summary>
public interface IServerConfigRenderer
{
    /// <summary>
    /// Renders the desired server config. For <see cref="KeyCustody.AgentManaged"/> the interface private
    /// key is held only by the agent, so the render omits the <c>PrivateKey</c> line (and never reveals a
    /// key) — the agent injects its own before bringing the interface up. (ADR-028)
    /// </summary>
    Task<Result<RenderedServerConfig>> RenderAsync(
        WireGuardInstance instance, string interfaceName, KeyCustody keyCustody, CancellationToken cancellationToken);
}

/// <summary>
/// Builds the deployable server config from desired state, decrypting keys just-in-time — so providers
/// (and the drift check) stay free of WireGuard internals. Shared by the deploy dispatcher and the
/// drift detector so "desired" means exactly the same bytes in both. (docs/12-remote-orchestration.md §4)
/// </summary>
public sealed class ServerConfigRenderer(
    IApplicationDbContext dbContext,
    IConfigurationService configuration,
    IKeyManagementService keys)
    : IServerConfigRenderer
{
    public async Task<Result<RenderedServerConfig>> RenderAsync(
        WireGuardInstance instance, string interfaceName, KeyCustody keyCustody, CancellationToken cancellationToken)
    {
        // AgentManaged: the agent holds the interface key — never reveal one (it may be absent) and render
        // key-less. WireHqManaged: reveal the stored key just-in-time, as before.
        string? privateKey = null;
        if (keyCustody == KeyCustody.WireHqManaged)
        {
            if (instance.PrivateKeyId is not { } privateKeyId)
            {
                return WireGuardErrors.Key.NotFound;
            }

            privateKey = await keys.RevealAsync(privateKeyId, cancellationToken);
            if (privateKey is null)
            {
                return WireGuardErrors.Key.NotFound;
            }
        }

        var peers = await dbContext.Set<Peer>()
            .Where(p => p.InstanceId == instance.Id && p.Status == PeerStatus.Active)
            .OrderBy(p => p.AssignedAddress)
            .ToListAsync(cancellationToken);

        var entries = new List<InstancePeerEntry>(peers.Count);
        foreach (var peer in peers)
        {
            var psk = peer.PresharedKeyId is { } pskId ? await keys.RevealAsync(pskId, cancellationToken) : null;
            entries.Add(new InstancePeerEntry(peer.PublicKey, psk, peer.AssignedAddress));
        }

        var text = configuration.RenderInstanceConfig(new InstanceConfigInput(
            privateKey, instance.InterfaceAddress, instance.ListenPort, instance.Mtu, entries));

        return new RenderedServerConfig(interfaceName, text);
    }
}
