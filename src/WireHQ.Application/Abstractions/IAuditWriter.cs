using WireHQ.Domain.Auditing;

namespace WireHQ.Application.Abstractions;

/// <summary>
/// Appends an audit entry to the current unit of work (it is persisted atomically with the
/// action it describes by the UnitOfWork behavior). The writer enriches each entry with the
/// actor, tenant, and request metadata from context, so call sites only state what happened.
/// (docs/04-security.md)
/// </summary>
public interface IAuditWriter
{
    void Record(
        string action,
        AuditOutcome outcome = AuditOutcome.Success,
        string? targetType = null,
        string? targetId = null,
        object? changes = null,
        // Override the correlation/request id for this entry. Defaults to the ambient request's id; pass an
        // explicit one when the action is driven by background/edge work that should chain to a different,
        // originating request (e.g. an agent job result chaining to the deploy that enqueued it). (ADR-030)
        string? correlationId = null,
        // Override the owning organisation. Defaults to the ambient tenant; pass an explicit one from an
        // ITenantUnscopedRequest, where there is no ambient tenant and the entry would otherwise be written with a
        // NULL organization_id — invisible to the org it belongs to, since the audit console filters on that column.
        // (The inbound SMS webhook is anonymous and resolves its org from the request itself.)
        Guid? organizationId = null);
}
