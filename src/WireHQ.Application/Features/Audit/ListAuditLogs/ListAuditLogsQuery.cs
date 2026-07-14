using Microsoft.EntityFrameworkCore;
using WireHQ.Application.Abstractions;
using WireHQ.Application.Abstractions.Persistence;
using WireHQ.Application.Authorization;
using WireHQ.Application.Common.Messaging;
using WireHQ.Application.Entitlements;
using WireHQ.Shared.Primitives;
using WireHQ.Shared.Results;

namespace WireHQ.Application.Features.Audit.ListAuditLogs;

/// <summary>
/// The tenant's own audit feed: keyset-paginated (opaque <see cref="Cursor"/>), richly filterable, and clamped
/// to the plan's visibility window. (docs/15 §5)
/// </summary>
public sealed record ListAuditLogsQuery(
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    string? Action = null,
    string? Category = null,
    string? Actor = null,
    string? Target = null,
    string? Outcome = null,
    string? Query = null,
    string? Cursor = null,
    int PageSize = AuditQuerying.DefaultPageSize)
    : IQuery<CursorPage<AuditLogItem>>, IAuthorizedRequest
{
    public IReadOnlyCollection<string> RequiredPermissions => [Permissions.Audit.Read];
}

public sealed record AuditLogItem(
    Guid Id,
    Guid? ActorUserId,
    string? ActorEmail,
    string ActorType,
    string Action,
    string Outcome,
    string? TargetType,
    string? TargetId,
    string? IpAddress,
    // The correlation reference (the W3C trace id, ADR-030) — quote it to tie this event to its logs + trace.
    string? CorrelationId,
    // The structured before/after diff (JSON) for the changes viewer; null for non-mutating events.
    string? Changes,
    DateTimeOffset OccurredAtUtc);

public sealed class ListAuditLogsQueryHandler(
    IApplicationDbContext dbContext,
    ITenantContext tenant,
    IEntitlementService entitlements,
    IDateTimeProvider clock)
    : IQueryHandler<ListAuditLogsQuery, CursorPage<AuditLogItem>>
{
    public async Task<Result<CursorPage<AuditLogItem>>> Handle(ListAuditLogsQuery query, CancellationToken cancellationToken)
    {
        var pageSize = AuditQuerying.ClampPageSize(query.PageSize);
        var filters = AuditLogFilters.Create(
            query.From, query.To, query.Action, query.Category, query.Actor, query.Target, query.Outcome, query.Query);

        // AuditLog.OrganizationId is nullable (platform events exist), so scope explicitly.
        var rows = dbContext.AuditLogs.Where(a => a.OrganizationId == tenant.OrganizationId);

        // Per-edition visibility: a tenant reads back only as far as its plan's retention window allows
        // (Community 30d / Pro 1y / Enterprise unlimited). Physical data may persist longer (the sweeper's
        // ceiling); this clamp is the customer-visible window. (docs/15 §5)
        if (await entitlements.AuditRetentionWindowAsync(cancellationToken) is { } window)
        {
            var floor = clock.UtcNow - window;
            rows = rows.Where(a => a.OccurredAtUtc >= floor);
        }

        var items = await rows
            .ApplyFilters(filters)
            .ApplyKeyset(AuditCursor.TryDecode(query.Cursor))
            .OrderByDescending(a => a.OccurredAtUtc)
            .ThenByDescending(a => a.Id)
            .Take(pageSize + 1)
            .Select(a => new AuditLogItem(
                a.Id,
                a.ActorUserId,
                a.ActorEmail,
                a.ActorType,
                a.Action,
                a.Outcome.ToString(),
                a.TargetType,
                a.TargetId,
                a.IpAddress,
                a.RequestId,
                a.Changes,
                a.OccurredAtUtc))
            .ToListAsync(cancellationToken);

        return items.ToCursorPage(pageSize, i => new AuditCursor(i.OccurredAtUtc, i.Id));
    }
}
