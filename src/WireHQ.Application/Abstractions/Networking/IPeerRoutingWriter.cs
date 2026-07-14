namespace WireHQ.Application.Abstractions.Networking;

/// <summary>
/// Sets peers' WireGuard <c>AllowedIPs</c> and deploys the change through the normal peer-deploy path (render +
/// version + provider apply). The write counterpart to <see cref="INetworkTopologyReader"/>: it exists so code
/// that cannot reference the WireGuard module (the SaaS Access Policies apply, docs/22 §6/§8) can push computed
/// routing without naming <c>Peer</c>. Runs inside the caller's unit of work. Module-neutral — carries no Access
/// Policies concepts, so the WireGuard module stays ignorant of Policy. Core + registered by the WireGuard
/// module; idle in the CE (its only consumer, the Policy apply command, is SaaS-only).
/// </summary>
public interface IPeerRoutingWriter
{
    /// <summary>Apply each assignment (set AllowedIPs + deploy). Returns the number of peers applied (a missing
    /// peer is skipped).</summary>
    Task<int> ApplyAsync(IReadOnlyList<PeerAllowedIpsAssignment> assignments, CancellationToken cancellationToken);
}

/// <summary>One peer's desired AllowedIPs.</summary>
public sealed record PeerAllowedIpsAssignment(Guid PeerId, IReadOnlyList<string> AllowedIps);
