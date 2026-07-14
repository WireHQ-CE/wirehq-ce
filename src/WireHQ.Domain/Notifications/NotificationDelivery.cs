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
/// (docs/35 blocker B-5). <see cref="RequiredFeature"/> is copied from the rule so the sender can re-check the live
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
        Guid id, Guid organizationId, Guid ruleId, Guid jobId, ChannelKind channel, string? requiredFeature,
        string recipient, string subject, string body, string? dedupValue, DateTimeOffset nowUtc)
        : base(id)
    {
        OrganizationId = organizationId;
        RuleId = ruleId;
        JobId = jobId;
        ChannelKind = channel;
        RequiredFeature = requiredFeature;
        Recipient = recipient;
        RenderedSubject = subject;
        RenderedBody = body;
        DedupValue = dedupValue;
        Status = NotificationDeliveryStatus.Pending;
        Attempts = 0;
        NextAttemptAtUtc = nowUtc; // first attempt is due immediately
        CreatedAtUtc = nowUtc;
    }

    public Guid OrganizationId { get; private set; }

    public Guid RuleId { get; private set; }

    public Guid JobId { get; private set; }

    public ChannelKind ChannelKind { get; private set; }

    /// <summary>The entitlement feature this delivery needs (null = free-core Email); re-checked by the sender.</summary>
    public string? RequiredFeature { get; private set; }

    /// <summary>The channel-specific address (email address, chat webhook is on the config, phone number).</summary>
    public string Recipient { get; private set; } = null!;

    public string RenderedSubject { get; private set; } = null!;

    public string RenderedBody { get; private set; } = null!;

    /// <summary>Idempotency value passed to provider-side dedup (SMS). Not a DB unique constraint (B-5).</summary>
    public string? DedupValue { get; private set; }

    public NotificationDeliveryStatus Status { get; private set; }

    public int Attempts { get; private set; }

    /// <summary>When the (next) attempt is due. Null once terminal.</summary>
    public DateTimeOffset? NextAttemptAtUtc { get; private set; }

    public int? LastResponseCode { get; private set; }

    public string? LastError { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset? DeliveredAtUtc { get; private set; }

    public static NotificationDelivery Create(
        Guid organizationId, Guid ruleId, Guid jobId, ChannelKind channel, string? requiredFeature,
        string recipient, string subject, string body, string? dedupValue, DateTimeOffset nowUtc) =>
        new(Guid.CreateVersion7(), organizationId, ruleId, jobId, channel, requiredFeature,
            recipient, Truncate(subject, MaxSubjectLength)!, body, dedupValue, nowUtc);

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
