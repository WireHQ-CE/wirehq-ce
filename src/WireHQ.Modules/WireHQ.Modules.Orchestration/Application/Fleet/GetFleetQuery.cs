using Microsoft.EntityFrameworkCore;
using WireHQ.Application.Abstractions;
using WireHQ.Application.Abstractions.Persistence;
using WireHQ.Application.Common.Messaging;
using WireHQ.Application.Entitlements;
using WireHQ.Modules.Orchestration.Domain;
using WireHQ.Modules.WireGuard.Authorization;
using WireHQ.Modules.WireGuard.Domain;
using WireHQ.Shared.Results;

namespace WireHQ.Modules.Orchestration.Application.Fleet;

/// <summary>
/// A cross-instance operational overview: every WireGuard instance with its deployment target, observed
/// status, config-drift verdict, and peer connectivity — plus a fleet-wide summary. Read-only and
/// tenant-scoped (every table is <c>ITenantOwned</c>). Powers the Fleet dashboard. (docs/12 §13 Phase 3)
/// </summary>
public sealed record GetFleetQuery : IQuery<FleetDto>, IAuthorizedRequest, IRequiresFeature
{
    public IReadOnlyCollection<string> RequiredPermissions => [WireGuardPermissions.Instances.Read];

    public string RequiredFeature => PlanFeatures.FleetDashboard;
}

public sealed record FleetSummary(
    int TotalInstances,
    int Running,
    int Degraded,
    int Drifted,
    int LocalTargets,
    int SshTargets,
    int AgentTargets,
    int AgentsTotal,
    int AgentsOnline,
    int PeersTotal,
    int PeersConnected);

public sealed record FleetInstance(
    Guid InstanceId,
    string Name,
    string Slug,
    string? NetworkName,
    string TargetKind,
    string? TargetName,
    string Status,
    bool HasDrift,
    DateTimeOffset? ObservedAtUtc,
    int PeersTotal,
    int PeersConnected,
    long RxBytes,
    long TxBytes,
    DateTimeOffset? AgentLastSeenAtUtc);

public sealed record FleetDto(FleetSummary Summary, IReadOnlyList<FleetInstance> Instances);

public sealed class GetFleetQueryHandler(IApplicationDbContext dbContext, IDateTimeProvider clock)
    : IQueryHandler<GetFleetQuery, FleetDto>
{
    // A peer/agent counts as "connected"/"online" if WireHQ has heard from it recently.
    private static readonly TimeSpan PeerConnectedWindow = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan AgentOnlineWindow = TimeSpan.FromMinutes(2);

    public async Task<Result<FleetDto>> Handle(GetFleetQuery query, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var peerCutoff = now - PeerConnectedWindow;
        var agentCutoff = now - AgentOnlineWindow;

        var instances = await dbContext.Set<WireGuardInstance>()
            .OrderBy(i => i.Name)
            .Select(i => new { i.Id, i.Name, Slug = i.Slug.Value, i.NetworkId, i.Status })
            .ToListAsync(cancellationToken);

        var networkNames = await dbContext.Set<WireGuardNetwork>()
            .Select(n => new { n.Id, n.Name })
            .ToDictionaryAsync(n => n.Id, n => n.Name, cancellationToken);

        var targets = await dbContext.Set<DeploymentTarget>()
            .Select(t => new { t.InstanceId, t.Kind, t.SshTargetId, t.AgentId })
            .ToListAsync(cancellationToken);
        var targetByInstance = targets.ToDictionary(t => t.InstanceId);

        var sshNames = await dbContext.Set<SshTarget>()
            .Select(s => new { s.Id, s.Name })
            .ToDictionaryAsync(s => s.Id, s => s.Name, cancellationToken);
        var agents = await dbContext.Set<Agent>()
            .Select(a => new { a.Id, a.Name, a.Status, a.LastSeenAtUtc })
            .ToListAsync(cancellationToken);
        var agentById = agents.ToDictionary(a => a.Id);

        var runtime = await dbContext.Set<InstanceRuntimeStatus>()
            .Select(r => new { r.InstanceId, r.HasDrift, r.ObservedAtUtc })
            .ToListAsync(cancellationToken);
        var runtimeByInstance = runtime.ToDictionary(r => r.InstanceId);

        var peerAggregates = (await dbContext.Set<Peer>()
            .Where(p => p.Status == PeerStatus.Active)
            .GroupBy(p => p.InstanceId)
            .Select(g => new
            {
                InstanceId = g.Key,
                Total = g.Count(),
                Connected = g.Count(p => p.LastHandshakeAtUtc != null && p.LastHandshakeAtUtc >= peerCutoff),
                Rx = g.Sum(p => p.RxBytes),
                Tx = g.Sum(p => p.TxBytes),
            })
            .ToListAsync(cancellationToken))
            .ToDictionary(p => p.InstanceId);

        var rows = new List<FleetInstance>(instances.Count);
        foreach (var i in instances)
        {
            targetByInstance.TryGetValue(i.Id, out var target);
            var kind = target?.Kind ?? DeploymentTargetKind.Local;

            string? targetName = null;
            DateTimeOffset? agentLastSeen = null;
            if (kind == DeploymentTargetKind.Ssh && target?.SshTargetId is { } sshId)
            {
                sshNames.TryGetValue(sshId, out targetName);
            }
            else if (kind == DeploymentTargetKind.Agent && target?.AgentId is { } agentId && agentById.TryGetValue(agentId, out var agent))
            {
                targetName = agent.Name;
                agentLastSeen = agent.LastSeenAtUtc;
            }

            runtimeByInstance.TryGetValue(i.Id, out var rt);
            peerAggregates.TryGetValue(i.Id, out var peers);

            rows.Add(new FleetInstance(
                i.Id, i.Name, i.Slug, networkNames.GetValueOrDefault(i.NetworkId),
                kind.ToString(), targetName, i.Status.ToString(),
                rt?.HasDrift ?? false, rt?.ObservedAtUtc,
                peers?.Total ?? 0, peers?.Connected ?? 0, peers?.Rx ?? 0, peers?.Tx ?? 0,
                agentLastSeen));
        }

        var summary = new FleetSummary(
            TotalInstances: rows.Count,
            Running: rows.Count(r => r.Status == nameof(InstanceStatus.Running)),
            Degraded: rows.Count(r => r.Status is nameof(InstanceStatus.Degraded) or nameof(InstanceStatus.Error)),
            Drifted: rows.Count(r => r.HasDrift),
            LocalTargets: rows.Count(r => r.TargetKind == nameof(DeploymentTargetKind.Local)),
            SshTargets: rows.Count(r => r.TargetKind == nameof(DeploymentTargetKind.Ssh)),
            AgentTargets: rows.Count(r => r.TargetKind == nameof(DeploymentTargetKind.Agent)),
            AgentsTotal: agents.Count,
            AgentsOnline: agents.Count(a => a.Status == AgentStatus.Active && a.LastSeenAtUtc >= agentCutoff),
            PeersTotal: rows.Sum(r => r.PeersTotal),
            PeersConnected: rows.Sum(r => r.PeersConnected));

        return new FleetDto(summary, rows);
    }
}
