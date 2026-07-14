using Microsoft.EntityFrameworkCore;
using WireHQ.Application.Abstractions.Networking;
using WireHQ.Application.Abstractions.Persistence;
using WireHQ.Domain.Memberships;
using WireHQ.Domain.Teams;
using WireHQ.Modules.WireGuard.Domain;

namespace WireHQ.Modules.WireGuard.Services;

/// <summary>
/// The WireGuard-module implementation of <see cref="INetworkTopologyReader"/> (docs/22-access-policies.md §6).
/// Materialises the active org's peers + networks — joining the module's own <c>Peer</c>/<c>WireGuardInstance</c>/
/// <c>WireGuardNetwork</c> to the core <c>Membership</c>/<c>TeamMember</c> identity — into the module-neutral
/// snapshot. Everything is tenant-scoped by the global query filters (no cross-org read). Returns only plain
/// DTOs, so it carries no Access Policies concepts: this module stays ignorant of Policy, and this reader is
/// core + idle in the CE (its only consumer, the SaaS Policy compiler, is stripped).
/// </summary>
public sealed class NetworkTopologyReader(IApplicationDbContext dbContext) : INetworkTopologyReader
{
    public async Task<NetworkTopologySnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        var networks = await dbContext.Set<WireGuardNetwork>().AsNoTracking()
            .Select(n => new { n.Id, n.Name, n.Cidr, Dns = n.Dns.ToList() })
            .ToListAsync(cancellationToken);

        var instances = await dbContext.Set<WireGuardInstance>().AsNoTracking()
            .Select(i => new { i.Id, i.NetworkId, i.InterfaceAddress, Dns = i.Dns.ToList() })
            .ToListAsync(cancellationToken);

        var peers = await dbContext.Set<Peer>().AsNoTracking()
            .Where(p => p.Status == PeerStatus.Active)
            .Select(p => new { p.Id, p.Name, p.InstanceId, p.MembershipId, p.AssignedAddress, AllowedIps = p.AllowedIps.ToList() })
            .ToListAsync(cancellationToken);

        // Identity annotations: each bound membership's roles (owned collection) + team memberships.
        var membershipIds = peers.Where(p => p.MembershipId is not null).Select(p => p.MembershipId!.Value).Distinct().ToList();

        var rolesByMembership = await dbContext.Set<Membership>().AsNoTracking()
            .Where(m => membershipIds.Contains(m.Id))
            .Select(m => new { m.Id, RoleIds = m.Roles.Select(r => r.RoleId).ToList() })
            .ToDictionaryAsync(x => x.Id, x => (IReadOnlyList<Guid>)x.RoleIds, cancellationToken);

        var teamsByMembership = (await dbContext.Set<TeamMember>().AsNoTracking()
                .Where(tm => membershipIds.Contains(tm.MembershipId))
                .Select(tm => new { tm.MembershipId, tm.TeamId })
                .ToListAsync(cancellationToken))
            .GroupBy(x => x.MembershipId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<Guid>)g.Select(x => x.TeamId).ToList());

        var instanceToNetwork = instances.ToDictionary(i => i.Id, i => i.NetworkId);

        var topologyPeers = peers
            .Where(p => instanceToNetwork.ContainsKey(p.InstanceId))
            .Select(p => new TopologyPeer(
                p.Id, p.Name, instanceToNetwork[p.InstanceId], p.MembershipId,
                StripPrefix(p.AssignedAddress),
                p.AllowedIps,
                p.MembershipId is { } m1 && rolesByMembership.TryGetValue(m1, out var roles) ? roles : [],
                p.MembershipId is { } m2 && teamsByMembership.TryGetValue(m2, out var teams) ? teams : []))
            .ToList();

        var hubsByNetwork = instances
            .GroupBy(i => i.NetworkId)
            .ToDictionary(g => g.Key, g => g.Select(i => StripPrefix(i.InterfaceAddress)).ToList());

        var instanceDnsByNetwork = instances
            .GroupBy(i => i.NetworkId)
            .ToDictionary(g => g.Key, g => g.SelectMany(i => i.Dns).ToList());

        var topologyNetworks = networks.Select(n =>
        {
            var dns = n.Dns
                .Concat(instanceDnsByNetwork.GetValueOrDefault(n.Id, []))
                .Where(d => !string.IsNullOrWhiteSpace(d))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            return new TopologyNetwork(n.Id, n.Name, n.Cidr, hubsByNetwork.GetValueOrDefault(n.Id, []), dns);
        }).ToList();

        return new NetworkTopologySnapshot(topologyPeers, topologyNetworks);
    }

    /// <summary>A stored tunnel address may carry a prefix (<c>10.8.0.5/32</c>); the snapshot exposes the bare
    /// host, and the compiler re-adds the right host prefix.</summary>
    private static string StripPrefix(string address)
    {
        var slash = address.IndexOf('/');
        return slash < 0 ? address.Trim() : address[..slash].Trim();
    }
}
