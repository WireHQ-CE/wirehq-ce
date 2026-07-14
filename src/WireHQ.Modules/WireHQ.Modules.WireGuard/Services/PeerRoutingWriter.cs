using Microsoft.EntityFrameworkCore;
using WireHQ.Application.Abstractions.Networking;
using WireHQ.Application.Abstractions.Persistence;
using WireHQ.Modules.WireGuard.Domain;

namespace WireHQ.Modules.WireGuard.Services;

/// <summary>
/// The WireGuard-module implementation of <see cref="IPeerRoutingWriter"/> (docs/22-access-policies.md §6/§8):
/// set each peer's <c>AllowedIps</c> and deploy the change via the shared <see cref="IPeerConfigApplier"/> — the
/// same render + version + provider-apply path a manual peer edit uses, so a policy apply and a hand edit behave
/// identically. Runs inside the caller's unit of work (mutations are saved by the caller's UoW behaviour).
/// Neutral — no Access Policies concepts; core + idle in the CE.
/// </summary>
public sealed class PeerRoutingWriter(IApplicationDbContext dbContext, IPeerConfigApplier peerConfig)
    : IPeerRoutingWriter
{
    public async Task<int> ApplyAsync(IReadOnlyList<PeerAllowedIpsAssignment> assignments, CancellationToken cancellationToken)
    {
        if (assignments.Count == 0)
        {
            return 0;
        }

        var ids = assignments.Select(a => a.PeerId).ToList();
        var peers = (await dbContext.Set<Peer>()
                .Where(p => ids.Contains(p.Id))
                .ToListAsync(cancellationToken))
            .ToDictionary(p => p.Id);

        var applied = 0;
        foreach (var assignment in assignments)
        {
            if (!peers.TryGetValue(assignment.PeerId, out var peer))
            {
                continue;
            }

            peer.SetAllowedIps(assignment.AllowedIps);
            var result = await peerConfig.ApplyAsync(peer, "policy", cancellationToken);
            if (result.IsFailure)
            {
                return applied; // surface via a partial count; the caller records the revision it did apply
            }

            applied++;
        }

        return applied;
    }
}
