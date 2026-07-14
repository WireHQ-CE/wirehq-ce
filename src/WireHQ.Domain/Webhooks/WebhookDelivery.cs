using WireHQ.Domain.Common;

namespace WireHQ.Domain.Webhooks;

/// <summary>
/// One queued/attempted webhook delivery — the <b>reliable outbox</b> row (docs/26-api-keys-webhooks.md §8, K-5).
/// Created <b>atomically with the audit entry that triggered it</b> (the <c>WebhookOutboxInterceptor</c> adds it in
/// the same unit of work), so a delivery can never be lost or fire before its cause commits. A background sender
/// drains <c>Pending</c> rows whose <see cref="NextAttemptAtUtc"/> is due, POSTs the signed body, and marks
/// <see cref="WebhookDeliveryStatus.Delivered"/> (2xx) or reschedules with exponential backoff up to
/// <see cref="MaxAttempts"/>, then <see cref="WebhookDeliveryStatus.Failed"/>. Tenant-owned (carries
/// <see cref="OrganizationId"/> so RLS covers it) in the reused <c>identity</c> schema. Also the customer-visible
/// delivery history.
/// </summary>
public sealed class WebhookDelivery : Entity, ITenantOwned
{
    public const int MaxAttempts = 6;
    public const int MaxErrorLength = 512;

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
    private WebhookDelivery()
    {
    }

    private WebhookDelivery(Guid id, Guid organizationId, Guid endpointId, string eventType, string payloadJson, DateTimeOffset nowUtc)
        : base(id)
    {
        OrganizationId = organizationId;
        EndpointId = endpointId;
        EventType = eventType;
        PayloadJson = payloadJson;
        Status = WebhookDeliveryStatus.Pending;
        Attempts = 0;
        NextAttemptAtUtc = nowUtc; // first attempt is due immediately
        CreatedAtUtc = nowUtc;
    }

    public Guid OrganizationId { get; private set; }

    public Guid EndpointId { get; private set; }

    /// <summary>The audit action name that triggered this delivery (e.g. <c>wg.peers.created</c>).</summary>
    public string EventType { get; private set; } = null!;

    public string PayloadJson { get; private set; } = null!;

    public WebhookDeliveryStatus Status { get; private set; }

    public int Attempts { get; private set; }

    /// <summary>When the (next) attempt is due. Null once terminal (delivered/failed).</summary>
    public DateTimeOffset? NextAttemptAtUtc { get; private set; }

    public int? LastResponseCode { get; private set; }

    public string? LastError { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset? DeliveredAtUtc { get; private set; }

    public static WebhookDelivery Create(Guid organizationId, Guid endpointId, string eventType, string payloadJson, DateTimeOffset nowUtc) =>
        new(Guid.CreateVersion7(), organizationId, endpointId, eventType, payloadJson, nowUtc);

    /// <summary>A 2xx response — done.</summary>
    public void MarkSucceeded(int responseCode, DateTimeOffset nowUtc)
    {
        Attempts++;
        Status = WebhookDeliveryStatus.Delivered;
        LastResponseCode = responseCode;
        LastError = null;
        DeliveredAtUtc = nowUtc;
        NextAttemptAtUtc = null;
    }

    /// <summary>A non-2xx or transport error — retry with backoff, or give up after <see cref="MaxAttempts"/>.</summary>
    public void MarkFailed(int? responseCode, string? error, DateTimeOffset nowUtc)
    {
        Attempts++;
        LastResponseCode = responseCode;
        LastError = Truncate(error);

        if (Attempts >= MaxAttempts)
        {
            Status = WebhookDeliveryStatus.Failed;
            NextAttemptAtUtc = null;
        }
        else
        {
            Status = WebhookDeliveryStatus.Pending;
            NextAttemptAtUtc = nowUtc + Backoff[Attempts - 1];
        }
    }

    /// <summary>Abandon the delivery (terminal, not retried) — e.g. its endpoint was disabled or removed before it
    /// could be sent. Keeps the sweep from re-selecting it forever and the outbox from growing unbounded.</summary>
    public void Cancel(string reason, DateTimeOffset nowUtc)
    {
        Status = WebhookDeliveryStatus.Failed;
        LastError = Truncate(reason);
        NextAttemptAtUtc = null;
    }

    public bool IsTerminal => Status is WebhookDeliveryStatus.Delivered or WebhookDeliveryStatus.Failed;

    private static string? Truncate(string? error) =>
        error is null ? null : error.Length <= MaxErrorLength ? error : error[..MaxErrorLength];
}

public enum WebhookDeliveryStatus
{
    Pending = 0,
    Delivered = 1,
    Failed = 2,
}
