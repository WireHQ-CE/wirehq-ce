using Microsoft.EntityFrameworkCore;
using WireHQ.Application.Abstractions.Persistence;
using WireHQ.Application.Common.Messaging;
using WireHQ.Modules.Orchestration.Domain;
using WireHQ.Modules.WireGuard.Authorization;
using WireHQ.Shared.Results;

namespace WireHQ.Modules.Orchestration.Application.Deployments;

/// <summary>The deployment-job history for an instance (most recent first).</summary>
public sealed record ListDeploymentsQuery(Guid InstanceId) : IQuery<IReadOnlyList<DeploymentSummary>>, IAuthorizedRequest
{
    public IReadOnlyCollection<string> RequiredPermissions => [WireGuardPermissions.Instances.Read];
}

public sealed record DeploymentSummary(
    Guid Id,
    string Type,
    string Status,
    int Attempts,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    string? Error);

public sealed class ListDeploymentsQueryHandler(IApplicationDbContext dbContext)
    : IQueryHandler<ListDeploymentsQuery, IReadOnlyList<DeploymentSummary>>
{
    public async Task<Result<IReadOnlyList<DeploymentSummary>>> Handle(ListDeploymentsQuery query, CancellationToken cancellationToken)
    {
        var rows = await dbContext.Set<DeploymentJob>()
            .Where(j => j.InstanceId == query.InstanceId)
            .OrderByDescending(j => j.CreatedAtUtc)
            .Select(j => new { j.Id, j.Type, j.Status, j.Attempts, j.CreatedAtUtc, j.CompletedAtUtc, j.Error })
            .ToListAsync(cancellationToken);

        IReadOnlyList<DeploymentSummary> items = rows
            .Select(j => new DeploymentSummary(j.Id, j.Type.ToString(), j.Status.ToString(), j.Attempts, j.CreatedAtUtc, j.CompletedAtUtc, j.Error))
            .ToList();

        return Result.Success(items);
    }
}
