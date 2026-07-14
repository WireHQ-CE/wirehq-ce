using Microsoft.EntityFrameworkCore;
using WireHQ.Application.Abstractions;
using WireHQ.Application.Abstractions.Persistence;
using WireHQ.Application.Authorization;
using WireHQ.Application.Common.Messaging;
using WireHQ.Application.Entitlements;
using WireHQ.Domain.Auditing;
using WireHQ.Shared.Results;

namespace WireHQ.Application.Features.Audit.ExportAuditLogs;

/// <summary>
/// Exports the tenant's (filtered, edition-clamped) audit feed for download — the full matching set, not a page —
/// in any of the supported <c>format</c>s (CSV/JSON for humans, OCSF/CEF for SIEM ingestion; the controller picks
/// the serializer). Gated by the <c>audit.export</c> entitlement (Enterprise) AND the <c>audit.logs.read</c>
/// permission. Bounded by <see cref="MaxRows"/> so a single export can't scan the whole partition tree into
/// memory — narrow by date range for more.
/// <para>
/// Modelled as a <see cref="ICommand{T}"/> (not a query) precisely so the read <b>audits itself</b>: an audit-log
/// export is a compliance-sensitive data egress, so the handler records an <c>audit.export</c> entry (the
/// audit-the-auditor pattern, ADR-032) naming the actor, format, filters, and row count — committed atomically
/// with the read by the UnitOfWork. (docs/15 §5/§11/§16, §19.4)
/// </para>
/// </summary>
public sealed record ExportAuditLogsCommand(
    string Format,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    string? Action = null,
    string? Category = null,
    string? Actor = null,
    string? Target = null,
    string? Outcome = null,
    string? Query = null)
    : ICommand<IReadOnlyList<AuditExportRow>>, IAuthorizedRequest, IRequiresFeature
{
    /// <summary>The most rows a single export returns; callers narrow by date range to export beyond this.</summary>
    public const int MaxRows = 50_000;

    public IReadOnlyCollection<string> RequiredPermissions => [Permissions.Audit.Read];

    public string RequiredFeature => PlanFeatures.AuditExport;
}

/// <summary>A flat, export-friendly audit row. Includes the <c>Changes</c> diff (omitted from the list feed).</summary>
public sealed record AuditExportRow(
    DateTimeOffset OccurredAtUtc,
    string Action,
    string? ActorEmail,
    string ActorType,
    string Outcome,
    string? TargetType,
    string? TargetId,
    string? IpAddress,
    string? CorrelationId,
    string? Changes);

public sealed class ExportAuditLogsCommandHandler(
    IApplicationDbContext dbContext,
    ITenantContext tenant,
    IEntitlementService entitlements,
    IDateTimeProvider clock,
    IAuditWriter audit)
    : ICommandHandler<ExportAuditLogsCommand, IReadOnlyList<AuditExportRow>>
{
    public async Task<Result<IReadOnlyList<AuditExportRow>>> Handle(ExportAuditLogsCommand command, CancellationToken cancellationToken)
    {
        var filters = AuditLogFilters.Create(
            command.From, command.To, command.Action, command.Category, command.Actor, command.Target, command.Outcome, command.Query);

        // AuditLog.OrganizationId is nullable (platform events exist), so scope explicitly.
        var rows = dbContext.AuditLogs.Where(a => a.OrganizationId == tenant.OrganizationId);

        // Same per-edition visibility clamp the list read applies — a tenant exports only what it can see. (docs/15 §5)
        if (await entitlements.AuditRetentionWindowAsync(cancellationToken) is { } window)
        {
            var floor = clock.UtcNow - window;
            rows = rows.Where(a => a.OccurredAtUtc >= floor);
        }

        var items = await rows
            .ApplyFilters(filters)
            .OrderByDescending(a => a.OccurredAtUtc)
            .ThenByDescending(a => a.Id)
            .Take(ExportAuditLogsCommand.MaxRows)
            .Select(a => new AuditExportRow(
                a.OccurredAtUtc,
                a.Action,
                a.ActorEmail,
                a.ActorType,
                a.Outcome.ToString(),
                a.TargetType,
                a.TargetId,
                a.IpAddress,
                a.RequestId,
                a.Changes))
            .ToListAsync(cancellationToken);

        // Audit-the-auditor: who exported the audit log, in what format, with which filters, and how much.
        // The UnitOfWork commits this entry atomically with the read; the chain interceptor links it. (§19.4)
        audit.Record(
            action: "audit.export",
            outcome: AuditOutcome.Success,
            changes: new
            {
                command.Format,
                command.From,
                command.To,
                command.Action,
                command.Category,
                command.Actor,
                command.Target,
                command.Outcome,
                command.Query,
                RowCount = items.Count,
            });

        return items;
    }
}
