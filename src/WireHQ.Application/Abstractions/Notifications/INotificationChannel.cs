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
/// which uses the operator's SMTP sender — docs/35 §4.3/B-7).</summary>
public sealed record ChannelSendRequest(
    Guid OrganizationId,
    string Recipient,
    string Subject,
    string Body,
    string? DedupValue,
    NotificationChannelConfig? Config);

/// <summary>The outcome of a channel send — the <c>WebhookSendResult</c> shape.</summary>
public sealed record ChannelSendResult(bool Success, int? StatusCode = null, string? Error = null)
{
    public static ChannelSendResult Ok(int? statusCode = null) => new(true, statusCode);

    public static ChannelSendResult Failed(string error, int? statusCode = null) => new(false, statusCode, error);
}
