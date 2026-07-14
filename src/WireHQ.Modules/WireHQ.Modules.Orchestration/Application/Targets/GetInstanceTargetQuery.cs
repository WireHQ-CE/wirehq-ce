using Microsoft.EntityFrameworkCore;
using WireHQ.Application.Abstractions.Persistence;
using WireHQ.Application.Common.Messaging;
using WireHQ.Modules.Orchestration.Domain;
using WireHQ.Modules.WireGuard.Authorization;
using WireHQ.Modules.WireGuard.Domain;
using WireHQ.Shared.Results;

namespace WireHQ.Modules.Orchestration.Application.Targets;

/// <summary>The deployment binding for an instance. Returns a Local default when none is set.</summary>
public sealed record GetInstanceTargetQuery(Guid InstanceId) : IQuery<InstanceTargetDto>, IAuthorizedRequest
{
    public IReadOnlyCollection<string> RequiredPermissions => [WireGuardPermissions.Instances.Read];
}

public sealed record InstanceTargetDto(
    Guid InstanceId,
    string Kind,
    Guid? SshTargetId,
    string? SshTargetName,
    Guid? AgentId,
    string? AgentName,
    string KeyCustody,
    bool AgentKeyPending,
    bool AutoReconverge,
    string InterfaceName,
    bool HasDrift,
    DateTimeOffset? DriftObservedAtUtc);

public sealed class GetInstanceTargetQueryHandler(IApplicationDbContext dbContext)
    : IQueryHandler<GetInstanceTargetQuery, InstanceTargetDto>
{
    public async Task<Result<InstanceTargetDto>> Handle(GetInstanceTargetQuery query, CancellationToken cancellationToken)
    {
        var target = await dbContext.Set<DeploymentTarget>()
            .FirstOrDefaultAsync(t => t.InstanceId == query.InstanceId, cancellationToken);

        // The last observed drift verdict (kept fresh by the status reconciler / on-demand refresh).
        var runtime = await dbContext.Set<InstanceRuntimeStatus>()
            .Where(r => r.InstanceId == query.InstanceId)
            .Select(r => new { r.HasDrift, r.ObservedAtUtc })
            .FirstOrDefaultAsync(cancellationToken);
        var hasDrift = runtime?.HasDrift ?? false;
        var driftObservedAtUtc = runtime?.ObservedAtUtc;

        if (target is null)
        {
            return new InstanceTargetDto(query.InstanceId, nameof(DeploymentTargetKind.Local), null, null, null, null, nameof(KeyCustody.WireHqManaged), false, false, DeploymentTarget.DefaultInterfaceName, hasDrift, driftObservedAtUtc);
        }

        string? sshTargetName = null;
        if (target.SshTargetId is { } sshTargetId)
        {
            sshTargetName = await dbContext.Set<SshTarget>()
                .Where(t => t.Id == sshTargetId)
                .Select(t => t.Name)
                .FirstOrDefaultAsync(cancellationToken);
        }

        string? agentName = null;
        if (target.AgentId is { } agentId)
        {
            agentName = await dbContext.Set<Agent>()
                .Where(a => a.Id == agentId)
                .Select(a => a.Name)
                .FirstOrDefaultAsync(cancellationToken);
        }

        // AgentManaged + a WireHQ private key still on the instance ⇒ the agent hasn't reported its
        // interface key yet (the first deploy adopts it and scrubs the WireHQ key).
        var agentKeyPending = target.KeyCustody == KeyCustody.AgentManaged
            && await dbContext.Set<WireGuardInstance>()
                .Where(i => i.Id == target.InstanceId)
                .Select(i => i.PrivateKeyId)
                .FirstOrDefaultAsync(cancellationToken) is not null;

        return new InstanceTargetDto(target.InstanceId, target.Kind.ToString(), target.SshTargetId, sshTargetName, target.AgentId, agentName, target.KeyCustody.ToString(), agentKeyPending, target.AutoReconverge, target.InterfaceName, hasDrift, driftObservedAtUtc);
    }
}
