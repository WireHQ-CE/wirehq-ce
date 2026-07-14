using System.Text.Json;
using WireHQ.Application.Abstractions;
using WireHQ.Application.Abstractions.Persistence;
using WireHQ.Domain.Auditing;

namespace WireHQ.Infrastructure.Auditing;

/// <summary>
/// Appends audit entries to the current unit of work, enriched with the actor, tenant, and
/// request metadata from context. Persisted atomically with the action by the UnitOfWork
/// behavior, so an audited action and its record commit together or not at all.
/// </summary>
public sealed class AuditWriter(
    IApplicationDbContext dbContext,
    ICurrentUser currentUser,
    ITenantContext tenant,
    IRequestContext request,
    IDateTimeProvider clock)
    : IAuditWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public void Record(
        string action,
        AuditOutcome outcome = AuditOutcome.Success,
        string? targetType = null,
        string? targetId = null,
        object? changes = null,
        string? correlationId = null)
    {
        var entry = AuditLog.Record(
            action: action,
            outcome: outcome,
            organizationId: tenant.OrganizationId,
            actorUserId: currentUser.UserId,
            // Mark actions performed under impersonation distinctly; the platform.impersonation.started
            // audit event carries which operator is acting (and over which session). An API-key request has no
            // user identity — attribute it to the key (api_key) rather than a phantom user. (docs/26 §5)
            actorType: !currentUser.IsAuthenticated ? "anonymous"
                : currentUser.ApiKeyId is not null ? "api_key"
                : currentUser.ImpersonatorUserId is null ? "user" : "impersonation",
            targetType: targetType,
            targetId: targetId,
            changes: changes is null ? null : JsonSerializer.Serialize(changes, JsonOptions),
            // Snapshot the actor's email (+ impersonator) at write time so the record survives user deletion (docs/15 §5).
            actorEmail: currentUser.Email,
            impersonatorUserId: currentUser.ImpersonatorUserId,
            ipAddress: request.IpAddress,
            userAgent: request.UserAgent,
            // An explicit correlation id (background/edge work chaining to its originating request) wins;
            // otherwise the ambient request's id. (ADR-030)
            requestId: correlationId ?? request.RequestId,
            occurredAtUtc: clock.UtcNow);

        dbContext.AuditLogs.Add(entry);
    }
}
