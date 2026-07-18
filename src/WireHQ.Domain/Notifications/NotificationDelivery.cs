using WireHQ.Domain.Common;

namespace WireHQ.Domain.Notifications;

/// <summary>
/// One queued/attempted notification delivery to a single recipient over one channel (docs/35-notifications.md
/// §4.1/§4.5) — the <b>outbox row</b>, mirroring <c>WebhookDelivery</c>'s state machine exactly. Created by the
/// background drain when it expands a <see cref="NotificationJob"/> (NOT on the business save path), so a bulk
/// operation can never explode the triggering transaction. A background sender drains <c>Pending</c> rows whose
/// <see cref="NextAttemptAtUtc"/> is due, sends via the channel adapter, and marks
/// <see cref="NotificationDeliveryStatus.Delivered"/> or reschedules with exponential backoff up to
/// <see cref="MaxAttempts"/>. Tenant-owned in the reused <c>identity</c> schema; also the delivery history.
/// <para>
/// <see cref="DedupValue"/> is a plain value (a provider idempotency key for SMS), <b>not</b> a unique constraint —
/// a DB uniqueness check on a row the interceptor path can reach would risk failing the business transaction
/// (docs/35 blocker B-5). <see cref="RequiredFeatures"/> is copied from the rule so the sender can re-check the live
/// entitlement union before sending (MM-14) without loading the rule.
/// </para>
/// </summary>
public sealed class NotificationDelivery : Entity, ITenantOwned
{
    public const int MaxAttempts = 6;
    public const int MaxErrorLength = 512;
    public const int MaxRecipientLength = 320;
    public const int MaxSubjectLength = 256;

    /// <summary>Backoff before the 2nd..6th attempt (after the 1st..5th failure): 30s, 2m, 10m, 1h, 6h.</summary>
    private static readonly TimeSpan[] Backoff =
    [
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(10),
        TimeSpan.FromHours(1),
        TimeSpan.FromHours(6),
    ];

    // EF Core
    private NotificationDelivery()
    {
    }

    private NotificationDelivery(
        Guid id, Guid organizationId, Guid ruleId, Guid jobId, ChannelKind channel, IReadOnlyCollection<string> requiredFeatures,
        string recipient, string subject, string body, string? dedupValue, DateTimeOffset nowUtc,
        TimeOnly? quietHoursStart, TimeOnly? quietHoursEnd, string? quietHoursTimeZone, int escalationLevel)
        : base(id)
    {
        OrganizationId = organizationId;
        RuleId = ruleId;
        JobId = jobId;
        ChannelKind = channel;
        RequiredFeatures = requiredFeatures;
        Recipient = recipient;
        RenderedSubject = subject;
        RenderedBody = body;
        DedupValue = dedupValue;
        QuietHoursStart = quietHoursStart;
        QuietHoursEnd = quietHoursEnd;
        QuietHoursTimeZone = quietHoursTimeZone;
        EscalationLevel = escalationLevel;
        Status = NotificationDeliveryStatus.Pending;
        Attempts = 0;
        NextAttemptAtUtc = nowUtc; // first attempt is due immediately
        CreatedAtUtc = nowUtc;
    }

    public Guid OrganizationId { get; private set; }

    public Guid RuleId { get; private set; }

    public Guid JobId { get; private set; }

    public ChannelKind ChannelKind { get; private set; }

    /// <summary>The entitlement feature keys, <b>all</b> of which this delivery needs (empty = free-core Email);
    /// re-checked by the sender against the live union before every send (MM-14) — set-valued so revoking any one of
    /// a rule's modules (e.g. the Chat channel OR the routing module) stops it. Stored delimited; exposed as a set.</summary>
    public IReadOnlyCollection<string> RequiredFeatures { get; private set; } = Array.Empty<string>();

    /// <summary>The channel-specific address (email address, chat webhook is on the config, phone number).</summary>
    public string Recipient { get; private set; } = null!;

    public string RenderedSubject { get; private set; } = null!;

    public string RenderedBody { get; private set; } = null!;

    /// <summary>Idempotency value passed to provider-side dedup (SMS). Not a DB unique constraint (B-5).</summary>
    public string? DedupValue { get; private set; }

    /// <summary>The rule's quiet-hours window <b>copied at expand</b> (docs/35 §5, Wave 3) so the send path can defer
    /// without a rule load. Null (all three) = no quiet hours — a free-core rule, or a test send, is never deferred.</summary>
    public TimeOnly? QuietHoursStart { get; private set; }

    public TimeOnly? QuietHoursEnd { get; private set; }

    public string? QuietHoursTimeZone { get; private set; }

    /// <summary>Which escalation level produced this delivery (docs/35 §5): <b>0 = the primary</b>, N = escalation step N.
    /// The drain groups a job's deliveries by level to decide whether the current level has been reached.</summary>
    public int EscalationLevel { get; private set; }

    public NotificationDeliveryStatus Status { get; private set; }

    public int Attempts { get; private set; }

    /// <summary>When the (next) attempt is due. Null once terminal.</summary>
    public DateTimeOffset? NextAttemptAtUtc { get; private set; }

    public int? LastResponseCode { get; private set; }

    public string? LastError { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset? DeliveredAtUtc { get; private set; }

    public static NotificationDelivery Create(
        Guid organizationId, Guid ruleId, Guid jobId, ChannelKind channel, IReadOnlyCollection<string> requiredFeatures,
        string recipient, string subject, string body, string? dedupValue, DateTimeOffset nowUtc,
        TimeOnly? quietHoursStart = null, TimeOnly? quietHoursEnd = null, string? quietHoursTimeZone = null,
        int escalationLevel = 0) =>
        new(Guid.CreateVersion7(), organizationId, ruleId, jobId, channel, requiredFeatures,
            recipient, Truncate(subject, MaxSubjectLength)!, body, dedupValue, nowUtc,
            quietHoursStart, quietHoursEnd, quietHoursTimeZone, escalationLevel);

    /// <summary>A successful send — done.</summary>
    public void MarkSucceeded(int? responseCode, DateTimeOffset nowUtc)
    {
        Attempts++;
        Status = NotificationDeliveryStatus.Delivered;
        LastResponseCode = responseCode;
        LastError = null;
        DeliveredAtUtc = nowUtc;
        NextAttemptAtUtc = null;
    }

    /// <summary>A send failure — retry with backoff, or give up after <see cref="MaxAttempts"/>.</summary>
    public void MarkFailed(int? responseCode, string? error, DateTimeOffset nowUtc)
    {
        Attempts++;
        LastResponseCode = responseCode;
        LastError = Truncate(error, MaxErrorLength);

        if (Attempts >= MaxAttempts)
        {
            Status = NotificationDeliveryStatus.Failed;
            NextAttemptAtUtc = null;
        }
        else
        {
            Status = NotificationDeliveryStatus.Pending;
            NextAttemptAtUtc = nowUtc + Backoff[Attempts - 1];
        }
    }

    /// <summary>Abandon the delivery (terminal, not retried, a distinct outcome) — e.g. its rule was deleted or the
    /// channel's module was deactivated (docs/35 §4.4). Keeps the sweep from re-selecting it forever.</summary>
    public void Cancel(string reason)
    {
        Status = NotificationDeliveryStatus.Cancelled;
        LastError = Truncate(reason, MaxErrorLength);
        NextAttemptAtUtc = null;
    }

    /// <summary>Hold a not-yet-sent delivery until <paramref name="untilUtc"/> because its rule is in quiet hours
    /// (docs/35 §5). A deferral is NOT a failed attempt — <see cref="Attempts"/>, <see cref="Status"/> (stays
    /// <see cref="NotificationDeliveryStatus.Pending"/>) and <see cref="CreatedAtUtc"/> are untouched; only the due time
    /// moves. No-op once terminal.</summary>
    public void Defer(DateTimeOffset untilUtc)
    {
        if (IsTerminal)
        {
            return;
        }

        NextAttemptAtUtc = untilUtc;
    }

    /// <summary>If this delivery's copied quiet-hours window is active at <paramref name="nowUtc"/>, the UTC instant it
    /// should be deferred to (the window's end); otherwise null (send now). Always null when no window was copied — a
    /// free-core rule or a test send.</summary>
    public DateTimeOffset? QuietDeferUntil(DateTimeOffset nowUtc) =>
        QuietHours.DeferUntil(QuietHoursStart, QuietHoursEnd, QuietHoursTimeZone, nowUtc);

    public bool IsTerminal =>
        Status is NotificationDeliveryStatus.Delivered or NotificationDeliveryStatus.Failed or NotificationDeliveryStatus.Cancelled;

    private static string? Truncate(string? value, int max) =>
        value is null ? null : value.Length <= max ? value : value[..max];
}

public enum NotificationDeliveryStatus
{
    Pending = 0,
    Delivered = 1,
    Failed = 2,
    Cancelled = 3,
}
