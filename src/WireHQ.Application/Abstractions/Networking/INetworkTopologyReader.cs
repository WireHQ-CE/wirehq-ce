namespace WireHQ.Application.Abstractions.Networking;

/// <summary>
/// Reads the active organization's WireGuard topology as a flat, module-neutral snapshot — peers (with their
/// bound identity: membership, roles, teams) and networks (subnet + hub addresses + DNS). It exists so code
/// that cannot reference the WireGuard module (e.g. the SaaS Access Policies compiler in
/// <c>Application/Features/Policy</c>) can still read the topology: the implementation lives in the WireGuard
/// module (which owns <c>Peer</c>/<c>WireGuardInstance</c>/<c>WireGuardNetwork</c>) and returns only these plain
/// DTOs. The port carries <b>no</b> Access Policies concepts, so the WireGuard module stays ignorant of Policy
/// (docs/22-access-policies.md §6). Core + registered by the WireGuard module; idle in the CE (its only consumer,
/// the Policy compiler, is SaaS-only).
/// </summary>
public interface INetworkTopologyReader
{
    Task<NetworkTopologySnapshot> ReadAsync(CancellationToken cancellationToken);
}

/// <summary>The org's WireGuard topology at a point in time (all active peers + all networks).</summary>
public sealed record NetworkTopologySnapshot(
    IReadOnlyList<TopologyPeer> Peers,
    IReadOnlyList<TopologyNetwork> Networks);

/// <summary>
/// An active peer, annotated with the identity it's bound to. <see cref="Address"/> is the peer's tunnel address
/// (a bare host IP, e.g. <c>10.8.0.5</c>). <see cref="RoleIds"/>/<see cref="TeamIds"/> come from the peer's
/// membership (empty when the peer isn't bound to a person).
/// </summary>
public sealed record TopologyPeer(
    Guid PeerId,
    string Name,
    Guid NetworkId,
    Guid? MembershipId,
    string Address,
    IReadOnlyList<string> CurrentAllowedIps,
    IReadOnlyList<Guid> RoleIds,
    IReadOnlyList<Guid> TeamIds);

/// <summary>
/// A WireGuard network. <see cref="Cidr"/> is the address pool (the routed subnet); <see cref="HubAddresses"/>
/// are its instances' interface addresses (the servers a peer must always reach — the connectivity floor);
/// <see cref="Dns"/> are the DNS servers advertised on the tunnel.
/// </summary>
public sealed record TopologyNetwork(
    Guid NetworkId,
    string Name,
    string Cidr,
    IReadOnlyList<string> HubAddresses,
    IReadOnlyList<string> Dns);
