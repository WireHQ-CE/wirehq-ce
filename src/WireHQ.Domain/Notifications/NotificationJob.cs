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

    private NotificationJob(
        Guid id, Guid organizationId, Guid ruleId, Guid? auditLogId, string action, string summarySnapshot,
        DigestCadence digestCadence, DateTimeOffset nowUtc)
        : base(id)
    {
        OrganizationId = organizationId;
        RuleId = ruleId;
        AuditLogId = auditLogId;
        Action = action;
        SummarySnapshot = summarySnapshot;
        DigestCadence = digestCadence;
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

    /// <summary>The matched rule's digest cadence, copied at capture (docs/35 §4.5). Denormalised so the immediate
    /// expand query can EXCLUDE digest jobs at the SQL level (<c>DigestCadence == Immediate</c>) rather than skipping
    /// them in a loop, which would livelock the ordered expand batch. Re-stamped if the rule's cadence changes.</summary>
    public DigestCadence DigestCadence { get; private set; }

    public NotificationJobStatus Status { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    // --- Escalation state (docs/35 §5, Wave 3) — only meaningful while Status == Escalating ---

    /// <summary>The current escalation level: 0 = the primary. Each fired step advances it by one.</summary>
    public int EscalationLevel { get; private set; }

    /// <summary>The rule's escalation-step count, denormalised at expand so the drain's due-scan is SQL-filterable (the
    /// DigestCadence precedent — no rule join). The chain is exhausted when <see cref="EscalationLevel"/> reaches it.</summary>
    public int EscalationStepCount { get; private set; }

    /// <summary>When the NEXT escalation step is due to fire (the last fire time + that step's delay). Null once the
    /// chain is settled / exhausted / acknowledged. The drain selects <c>Escalating</c> jobs whose cursor is due.</summary>
    public DateTimeOffset? EscalationNextDueAtUtc { get; private set; }

    /// <summary>When a human acknowledged the alert — stops the chain (docs/35 §5). Null = never acknowledged.</summary>
    public DateTimeOffset? AcknowledgedAtUtc { get; private set; }

    public Guid? AcknowledgedBy { get; private set; }

    /// <summary>True while this job is actively escalating — the only state the acknowledge endpoint acts on.</summary>
    public bool IsEscalating => Status == NotificationJobStatus.Escalating;

    public static NotificationJob Create(
        Guid organizationId, Guid ruleId, Guid? auditLogId, string action, string summarySnapshot,
        DigestCadence digestCadence, DateTimeOffset nowUtc) =>
        new(Guid.CreateVersion7(), organizationId, ruleId, auditLogId, action, Truncate(summarySnapshot, MaxSummaryLength), digestCadence, nowUtc);

    /// <summary>The drain expanded this job into per-recipient deliveries (or coalesced it into a digest).</summary>
    public void MarkExpanded() => Status = NotificationJobStatus.Expanded;

    /// <summary>The drain resolved the job to zero deliveries (rule gone/disabled, un-entitled, empty audience).</summary>
    public void MarkSkipped() => Status = NotificationJobStatus.Skipped;

    /// <summary>The primary was expanded AND the rule has an escalation chain (docs/35 §5): stay LIVE (Escalating) so the
    /// drain can fire the next step when <paramref name="firstStepDueUtc"/> (= now + step[0]'s delay) arrives.</summary>
    public void BeginEscalating(int stepCount, DateTimeOffset firstStepDueUtc)
    {
        Status = NotificationJobStatus.Escalating;
        EscalationStepCount = stepCount;
        EscalationLevel = 0;
        EscalationNextDueAtUtc = firstStepDueUtc;
    }

    /// <summary>The drain fired the step at the current level and advances to the next. <paramref name="nextStepDueUtc"/>
    /// is when the FOLLOWING step comes due, or null when the chain is now exhausted — in which case the job settles
    /// (back to <see cref="NotificationJobStatus.Expanded"/>), since there is nothing left to fire.</summary>
    public void AdvanceEscalation(DateTimeOffset? nextStepDueUtc)
    {
        EscalationLevel++;
        EscalationNextDueAtUtc = nextStepDueUtc;
        if (nextStepDueUtc is null)
        {
            Status = NotificationJobStatus.Expanded;
        }
    }

    /// <summary>Stop the chain without acknowledgement — the module was deactivated, or nothing is left to do.</summary>
    public void SettleEscalation()
    {
        Status = NotificationJobStatus.Expanded;
        EscalationNextDueAtUtc = null;
    }

    /// <summary>Acknowledged (docs/35 §5) — record who/when and stop the chain. <paramref name="userId"/> is nullable:
    /// a non-user principal (e.g. an API key) records null rather than a fabricated <see cref="Guid.Empty"/>.</summary>
    public void Acknowledge(Guid? userId, DateTimeOffset nowUtc)
    {
        AcknowledgedAtUtc = nowUtc;
        AcknowledgedBy = userId;
        Status = NotificationJobStatus.Expanded;
        EscalationNextDueAtUtc = null;
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];
}

public enum NotificationJobStatus
{
    Pending = 0,
    Expanded = 1,
    Skipped = 2,

    /// <summary>The primary was expanded AND its rule has an escalation chain: the job stays "live" so the drain's
    /// <c>EscalateAsync</c> phase can fire the next step when it comes due (docs/35 §5, Wave 3). Leaves this state —
    /// back to <see cref="Expanded"/> — on acknowledge, on chain exhaustion, or when the module is deactivated.</summary>
    Escalating = 3,
}
