using WireHQ.Domain.Notifications;

namespace WireHQ.Application.Abstractions.Notifications;

/// <summary>
/// The single seam every delivery channel implements (docs/35-notifications.md §4.3). The dispatch scheduler is
/// channel-agnostic: it resolves the adapter for a delivery's <see cref="ChannelKind"/> from a keyed registry and
/// calls <see cref="SendAsync"/>. A new channel = implement this + register it under its <see cref="Kind"/>. Channel
/// specifics (SMTP, chat Block Kit + its SSRF-guarded client, SMS provider REST) live only behind this port, so each
/// channel stays cleanly extractable to a future plugin module.
/// </summary>
public interface INotificationChannel
{
    ChannelKind Kind { get; }

    Task<ChannelSendResult> SendAsync(ChannelSendRequest request, CancellationToken cancellationToken);
}

/// <summary>One rendered message to deliver. <see cref="Config"/> is the per-org channel config (null for Email,
/// which uses the operator's SMTP sender — docs/35 §4.3/B-7). <see cref="DeliveryId"/> is the outbox row's id: SMS
/// needs it to build the per-message status-callback URL the provider reports back to (Wave 4 W4), which is how a send
/// whose response was lost is later resolved to what actually happened.</summary>
public sealed record ChannelSendRequest(
    Guid OrganizationId,
    string Recipient,
    string Subject,
    string Body,
    string? DedupValue,
    NotificationChannelConfig? Config,
    // No default: a caller that forgets this still compiles a working send, but one whose lost response can never be
    // resolved — a silent downgrade of the whole feature. Make the compiler ask.
    Guid DeliveryId);

/// <summary>The outcome of a channel send — the <c>WebhookSendResult</c> shape, with two terminal outcomes beyond
/// success and retryable failure. <see cref="IsSuppressed"/>: the recipient/carrier has opted out (e.g. Twilio error
/// 21610), so the scheduler <c>Cancel</c>s rather than retrying 6× on the backoff ladder (docs/36 §7).
/// <see cref="IsIndeterminate"/>: the request left the process and its outcome was never learned, so the scheduler must
/// NOT retry (docs/36 §7/§12, Wave 4 W4). <see cref="ProviderMessageId"/> is recorded on the delivery when the provider
/// named one.</summary>
public sealed record ChannelSendResult(
    bool Success, int? StatusCode = null, string? Error = null, bool IsSuppressed = false,
    bool IsIndeterminate = false, string? ProviderMessageId = null)
{
    public static ChannelSendResult Ok(int? statusCode = null, string? providerMessageId = null) =>
        new(true, statusCode, ProviderMessageId: providerMessageId);

    public static ChannelSendResult Failed(string error, int? statusCode = null) => new(false, statusCode, error);

    /// <summary>A terminal, non-retryable suppression (opt-out / carrier STOP). The scheduler maps this to
    /// <c>delivery.Cancel</c>, never <c>MarkFailed</c> — retrying an opted-out number would re-hammer it (docs/36 §7).</summary>
    public static ChannelSendResult Suppressed(string reason) => new(false, Error: reason, IsSuppressed: true);

    /// <summary>
    /// Terminal-unknown: we handed the message over and never learned what happened to it. The scheduler marks the
    /// delivery <c>Unresolved</c> rather than retrying — a blind resend would charge the operator twice and text a real
    /// person twice. Safe to stop because escalation is acknowledgement-driven and outcome-independent (docs/36 §12).
    /// </summary>
    public static ChannelSendResult Indeterminate(string reason, string? providerMessageId = null) =>
        new(false, Error: reason, IsIndeterminate: true, ProviderMessageId: providerMessageId);
}
