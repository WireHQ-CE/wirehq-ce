using Microsoft.EntityFrameworkCore;
using WireHQ.Application.Abstractions;
using WireHQ.Application.Abstractions.Persistence;
using WireHQ.Application.Common.Messaging;
using WireHQ.Domain.Auditing;
using WireHQ.Modules.Orchestration.Domain;
using WireHQ.Modules.WireGuard.Authorization;
using WireHQ.Modules.WireGuard.Domain;
using WireHQ.Shared.Observability;
using WireHQ.Shared.Results;

namespace WireHQ.Modules.Orchestration.Application.Deployments;

/// <summary>
/// Enqueues a deployment job for an instance's current desired state. The job is committed atomically
/// with its audit entry; the background dispatcher claims and enacts it via the instance's provider.
/// For the config-only Local provider this is a no-op success (the desired model is the source of
/// truth) — but it exercises and records the full pipeline. (docs/12-remote-orchestration.md §4)
/// </summary>
public sealed record RequestDeploymentCommand(Guid InstanceId) : ICommand<RequestDeploymentResponse>, IAuthorizedRequest
{
    public IReadOnlyCollection<string> RequiredPermissions => [WireGuardPermissions.Instances.Manage];
}

public sealed record RequestDeploymentResponse(Guid JobId, string Status);

public sealed class RequestDeploymentCommandHandler(
    IApplicationDbContext dbContext,
    ITenantContext tenant,
    IDateTimeProvider clock,
    IAuditWriter audit)
    : ICommandHandler<RequestDeploymentCommand, RequestDeploymentResponse>
{
    public async Task<Result<RequestDeploymentResponse>> Handle(RequestDeploymentCommand command, CancellationToken cancellationToken)
    {
        if (tenant.OrganizationId is not { } organizationId)
        {
            return Error.Forbidden("auth.tenant_required", "No active organization.");
        }

        var instance = await dbContext.Set<WireGuardInstance>()
            .FirstOrDefaultAsync(i => i.Id == command.InstanceId, cancellationToken);
        if (instance is null)
        {
            return OrchestrationErrors.Deployment.InstanceNotFound;
        }

        // Record which interface config version this deploy targets (informational/traceable).
        var desiredConfigVersion = await dbContext.Set<ConfigVersion>()
            .Where(c => c.TargetType == ConfigTargetType.Instance && c.TargetId == instance.Id)
            .MaxAsync(c => (int?)c.Version, cancellationToken);

        var idempotencyKey = $"deploy:{instance.Id:N}:{Guid.NewGuid():N}";
        var job = DeploymentJob.Queue(
            organizationId, instance.Id, DeploymentJobType.DeployConfig, desiredConfigVersion, idempotencyKey,
            CorrelationId.Current(), clock.UtcNow);
        dbContext.Set<DeploymentJob>().Add(job);

        audit.Record("deployment.queued", AuditOutcome.Success, nameof(DeploymentJob), job.Id.ToString(),
            new { instanceId = instance.Id, desiredConfigVersion });

        return new RequestDeploymentResponse(job.Id, job.Status.ToString());
    }
}
