using Microsoft.EntityFrameworkCore;
using WireHQ.Application.Abstractions.Persistence;
using WireHQ.Application.Common.Messaging;
using WireHQ.Modules.WireGuard.Authorization;
using WireHQ.Modules.WireGuard.Domain;
using WireHQ.Modules.WireGuard.Services;
using WireHQ.Shared.Results;

namespace WireHQ.Modules.WireGuard.Application.Enrollment;

/// <summary>
/// Dry-run a CSV upload: parse, validate, detect duplicates (in-file + against existing peers), and
/// show the address each new peer <b>would</b> get — with <b>no writes</b>. A query, so the UnitOfWork
/// behavior never persists anything. (docs/11-wireguard-module.md §7, steps 1–5)
/// </summary>
public sealed record ValidateEnrollmentQuery(Guid InstanceId, string CsvText)
    : IQuery<EnrollmentPreviewResult>, IAuthorizedRequest
{
    public IReadOnlyCollection<string> RequiredPermissions => [WireGuardPermissions.Enrollment.Manage];
}

public sealed record EnrollmentPreviewResult(
    int TotalRows,
    int CreateRows,
    int SkipRows,
    int ErrorRows,
    IReadOnlyList<EnrollmentPreviewRow> Rows);

public sealed record EnrollmentPreviewRow(
    int RowNumber,
    string? Name,
    string? Email,
    string? Department,
    string? DeviceType,
    string? AssignedAddress,
    IReadOnlyList<string> AllowedIps,
    string Outcome,
    string? Reason);

public sealed class ValidateEnrollmentQueryHandler(
    IApplicationDbContext dbContext,
    IEnrollmentService enrollment,
    IAddressAllocator addressAllocator)
    : IQueryHandler<ValidateEnrollmentQuery, EnrollmentPreviewResult>
{
    public async Task<Result<EnrollmentPreviewResult>> Handle(ValidateEnrollmentQuery query, CancellationToken cancellationToken)
    {
        var context = await EnrollmentContext.LoadAsync(dbContext, query.InstanceId, cancellationToken);
        if (context.IsFailure)
        {
            return context.Error;
        }

        var (instance, network, existingEmails, existingAddressHosts) = context.Value;

        var parsed = enrollment.Parse(query.CsvText);
        if (parsed.IsFailure)
        {
            return parsed.Error;
        }

        var plan = await EnrollmentPlanner.PlanAsync(
            parsed.Value, network.Cidr, existingEmails, existingAddressHosts, enrollment,
            (count, reserved, ct) => addressAllocator.AllocateManyAsync(instance.Id, instance.InterfaceAddress, network.Cidr, count, reserved, ct),
            cancellationToken);

        var rows = plan.Rows
            .Select(r => new EnrollmentPreviewRow(
                r.RowNumber, r.Row.Name, r.Row.Email, r.Row.Department, r.Row.DeviceType,
                r.AssignedAddress, r.Row.AllowedIps, r.Outcome.ToString(), r.Reason))
            .ToList();

        return new EnrollmentPreviewResult(plan.Rows.Count, plan.CreateCount, plan.SkipCount, plan.ErrorCount, rows);
    }
}
