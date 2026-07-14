using Microsoft.EntityFrameworkCore;
using WireHQ.Application.Abstractions.Persistence;
using WireHQ.Application.Common.Messaging;
using WireHQ.Modules.Orchestration.Authorization;
using WireHQ.Modules.Orchestration.Domain;
using WireHQ.Shared.Results;

namespace WireHQ.Modules.Orchestration.Application.Agents;

/// <summary>An enrolled agent's operator-facing details. The certificate is public; no secret is projected.</summary>
public sealed record AgentItem(
    Guid Id,
    string Name,
    string Status,
    string? Platform,
    string? Version,
    string CertificateFingerprint,
    DateTimeOffset EnrolledAtUtc,
    DateTimeOffset? LastSeenAtUtc,
    int ManagedInstances,
    int InstancesWithDrift);

public sealed record ListAgentsQuery : IQuery<IReadOnlyList<AgentItem>>, IAuthorizedRequest
{
    public IReadOnlyCollection<string> RequiredPermissions => [OrchestrationPermissions.Agents.Read];
}

public sealed class ListAgentsQueryHandler(IApplicationDbContext dbContext)
    : IQueryHandler<ListAgentsQuery, IReadOnlyList<AgentItem>>
{
    public async Task<Result<IReadOnlyList<AgentItem>>> Handle(ListAgentsQuery query, CancellationToken cancellationToken)
    {
        var agents = await dbContext.Set<Agent>()
            .OrderBy(a => a.Name)
            .Select(a => new
            {
                a.Id, a.Name, a.Status, a.Platform, a.Version, a.CertificateFingerprint, a.EnrolledAtUtc, a.LastSeenAtUtc,
            })
            .ToListAsync(cancellationToken);

        // Roll up the fleet: how many instances each agent manages, and how many are drifted. Both tables
        // are tenant-owned (RLS-scoped), and agent fleets are small, so an in-memory join is fine.
        var bindings = await dbContext.Set<DeploymentTarget>()
            .Where(t => t.Kind == DeploymentTargetKind.Agent && t.AgentId != null)
            .Select(t => new { AgentId = t.AgentId!.Value, t.InstanceId })
            .ToListAsync(cancellationToken);
        var driftedInstances = (await dbContext.Set<InstanceRuntimeStatus>()
            .Where(r => r.HasDrift)
            .Select(r => r.InstanceId)
            .ToListAsync(cancellationToken)).ToHashSet();

        var rows = agents.Select(a =>
        {
            var managed = bindings.Where(b => b.AgentId == a.Id).ToList();
            return new AgentItem(a.Id, a.Name, a.Status.ToString(), a.Platform, a.Version,
                a.CertificateFingerprint, a.EnrolledAtUtc, a.LastSeenAtUtc,
                managed.Count, managed.Count(b => driftedInstances.Contains(b.InstanceId)));
        }).ToList();

        return Result.Success<IReadOnlyList<AgentItem>>(rows);
    }
}

public sealed record GetAgentQuery(Guid Id) : IQuery<AgentItem>, IAuthorizedRequest
{
    public IReadOnlyCollection<string> RequiredPermissions => [OrchestrationPermissions.Agents.Read];
}

public sealed class GetAgentQueryHandler(IApplicationDbContext dbContext)
    : IQueryHandler<GetAgentQuery, AgentItem>
{
    public async Task<Result<AgentItem>> Handle(GetAgentQuery query, CancellationToken cancellationToken)
    {
        var a = await dbContext.Set<Agent>().FirstOrDefaultAsync(x => x.Id == query.Id, cancellationToken);
        if (a is null)
        {
            return OrchestrationErrors.Agent.NotFound;
        }

        var managedInstanceIds = await dbContext.Set<DeploymentTarget>()
            .Where(t => t.Kind == DeploymentTargetKind.Agent && t.AgentId == a.Id)
            .Select(t => t.InstanceId)
            .ToListAsync(cancellationToken);
        var withDrift = managedInstanceIds.Count == 0
            ? 0
            : await dbContext.Set<InstanceRuntimeStatus>()
                .CountAsync(r => managedInstanceIds.Contains(r.InstanceId) && r.HasDrift, cancellationToken);

        return new AgentItem(a.Id, a.Name, a.Status.ToString(), a.Platform, a.Version,
            a.CertificateFingerprint, a.EnrolledAtUtc, a.LastSeenAtUtc, managedInstanceIds.Count, withDrift);
    }
}
