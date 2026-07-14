using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WireHQ.Application.Abstractions.Persistence;
using WireHQ.Application.Common.Messaging;
using WireHQ.Modules.WireGuard.Authorization;
using WireHQ.Modules.WireGuard.Domain;
using WireHQ.Shared.Results;

namespace WireHQ.Modules.WireGuard.Application.Enrollment;

/// <summary>Returns an enrollment batch's status, counts, and per-row outcome summary.</summary>
public sealed record GetEnrollmentBatchQuery(Guid BatchId) : IQuery<EnrollmentBatchDetail>, IAuthorizedRequest
{
    public IReadOnlyCollection<string> RequiredPermissions => [WireGuardPermissions.Enrollment.Manage];
}

public sealed record EnrollmentBatchDetail(
    Guid Id,
    Guid InstanceId,
    string SourceFilename,
    string Status,
    int TotalRows,
    int ValidRows,
    int ErrorRows,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    IReadOnlyList<EnrollmentResultRow> Results);

public sealed class GetEnrollmentBatchQueryHandler(IApplicationDbContext dbContext)
    : IQueryHandler<GetEnrollmentBatchQuery, EnrollmentBatchDetail>
{
    public async Task<Result<EnrollmentBatchDetail>> Handle(GetEnrollmentBatchQuery query, CancellationToken cancellationToken)
    {
        var batch = await dbContext.Set<EnrollmentBatch>()
            .FirstOrDefaultAsync(b => b.Id == query.BatchId, cancellationToken);
        if (batch is null)
        {
            return WireGuardErrors.Enrollment.BatchNotFound;
        }

        var results = batch.Summary is { } summary
            ? JsonSerializer.Deserialize<List<EnrollmentResultRow>>(summary) ?? []
            : [];

        return new EnrollmentBatchDetail(
            batch.Id, batch.InstanceId, batch.SourceFilename, batch.Status.ToString(),
            batch.TotalRows, batch.ValidRows, batch.ErrorRows,
            batch.CreatedAtUtc, batch.CompletedAtUtc, results);
    }
}
