using Microsoft.EntityFrameworkCore;
using WireHQ.Application.Abstractions.Persistence;
using WireHQ.Application.Common.Messaging;
using WireHQ.Modules.Orchestration.Domain;
using WireHQ.Modules.WireGuard.Authorization;
using WireHQ.Shared.Results;

namespace WireHQ.Modules.Orchestration.Application.Deployments;

/// <summary>A single deployment job with its full timeline of events.</summary>
public sealed record GetDeploymentQuery(Guid JobId) : IQuery<DeploymentDetail>, IAuthorizedRequest
{
    public IReadOnlyCollection<string> RequiredPermissions => [WireGuardPermissions.Instances.Read];
}

public sealed record DeploymentDetail(
    Guid Id,
    Guid InstanceId,
    string Type,
    string Status,
    int Attempts,
    int? DesiredConfigVersion,
    string? Error,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? DispatchedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    IReadOnlyList<DeploymentEventItem> Events);

public sealed record DeploymentEventItem(string Phase, string? Detail, DateTimeOffset AtUtc);

public sealed class GetDeploymentQueryHandler(IApplicationDbContext dbContext)
    : IQueryHandler<GetDeploymentQuery, DeploymentDetail>
{
    public async Task<Result<DeploymentDetail>> Handle(GetDeploymentQuery query, CancellationToken cancellationToken)
    {
        var job = await dbContext.Set<DeploymentJob>()
            .Include(j => j.Events)
            .FirstOrDefaultAsync(j => j.Id == query.JobId, cancellationToken);
        if (job is null)
        {
            return OrchestrationErrors.Deployment.JobNotFound;
        }

        var events = job.Events
            .OrderBy(e => e.AtUtc)
            .Select(e => new DeploymentEventItem(e.Phase, e.Detail, e.AtUtc))
            .ToList();

        return new DeploymentDetail(
            job.Id, job.InstanceId, job.Type.ToString(), job.Status.ToString(), job.Attempts,
            job.DesiredConfigVersion, job.Error, job.CreatedAtUtc, job.DispatchedAtUtc, job.CompletedAtUtc, events);
    }
}
