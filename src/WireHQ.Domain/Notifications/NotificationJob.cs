using WireHQ.Domain.Common;

namespace WireHQ.Domain.Notifications;

/// <summary>
/// The <b>capture row</b> of the dispatch spine (docs/35-notifications.md §4.1, blocker B-1). When an audit event
/// matches a rule, the <c>NotificationOutboxInterceptor</c> inserts <b>one</b> of these into the <b>same unit of
/// work</b> as the triggering audit row — a pure in-memory cache match, <b>no user query and no throwing
/// constraint on the business save path</b>. Recipient expansion (which requires querying roles / opted-in users)
/// happens later, off the request path, in the background drain — so a bulk operation writing thousands of audit
/// rows can never explode the triggering transaction. Tenant-owned in the reused <c>identity</c> schema.
/// <para>
/// <see cref="SummarySnapshot"/> is a minimal, already-redacted summary captured at trigger time — <b>never</b> the
/// raw audit <c>Changes</c> JSON (docs/35 §4.6): notifications go to humans and shared channels, not machine
/// endpoints an admin controls.
/// </para>
/// </summary>
public sealed class NotificationJob : Entity, ITenantOwned
{
    public const int MaxActionLength = 128;
    public const int MaxSummaryLength = 2048;

    // EF Core
    private NotificationJob()
    {
    }

    private NotificationJob(Guid id, Guid organizationId, Guid ruleId, Guid? auditLogId, string action, string summarySnapshot, DateTimeOffset nowUtc)
        : base(id)
    {
        OrganizationId = organizationId;
        RuleId = ruleId;
        AuditLogId = auditLogId;
        Action = action;
        SummarySnapshot = summarySnapshot;
        Status = NotificationJobStatus.Pending;
        CreatedAtUtc = nowUtc;
    }

    public Guid OrganizationId { get; private set; }

    /// <summary>The rule that matched. SOFT reference (no FK) — the interceptor inserts from an in-memory cache
    /// that can lag a rule delete by a tick; a hard FK would fail the unrelated business transaction.</summary>
    public Guid RuleId { get; private set; }

    /// <summary>The audit row that triggered this (soft reference; null for the direct-enqueue secondary path).</summary>
    public Guid? AuditLogId { get; private set; }

    public string Action { get; private set; } = null!;

    /// <summary>A minimal, redacted human summary captured at trigger time — never the raw audit changes.</summary>
    public string SummarySnapshot { get; private set; } = null!;

    public NotificationJobStatus Status { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public static NotificationJob Create(Guid organizationId, Guid ruleId, Guid? auditLogId, string action, string summarySnapshot, DateTimeOffset nowUtc) =>
        new(Guid.CreateVersion7(), organizationId, ruleId, auditLogId, action, Truncate(summarySnapshot, MaxSummaryLength), nowUtc);

    /// <summary>The drain expanded this job into per-recipient deliveries.</summary>
    public void MarkExpanded() => Status = NotificationJobStatus.Expanded;

    /// <summary>The drain resolved the job to zero deliveries (rule gone/disabled, un-entitled, empty audience).</summary>
    public void MarkSkipped() => Status = NotificationJobStatus.Skipped;

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];
}

public enum NotificationJobStatus
{
    Pending = 0,
    Expanded = 1,
    Skipped = 2,
}
