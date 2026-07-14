using System.Text.Json.Serialization;

namespace WireHQ.Domain.Notifications;

/// <summary>
/// The delivery channels the Notifications subsystem can dispatch to (docs/35-notifications.md §4.3). Each is
/// implemented by an <c>INotificationChannel</c> adapter resolved by this kind. <see cref="Email"/> is free-core
/// and the only adapter built in Wave 1; <see cref="Chat"/> (Wave 2) and <see cref="Sms"/> (Wave 4) are gated
/// paid modules — the enum members exist for forward-compatibility so the model and migration are stable.
/// Serialized as a string on the API boundary (the house convention — the frontend sends/receives the name).
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ChannelKind
{
    Email = 0,
    Chat = 1,
    Sms = 2,
}

/// <summary>
/// Who a <see cref="NotificationRule"/> targets. v1 keeps audiences <b>internal + org-scoped</b> — an arbitrary
/// external <c>StaticAddress</c> is deliberately excluded (docs/35 N-9): it would be a data-exfiltration path
/// (route sensitive events to an attacker inbox with no receiver infrastructure).
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum NotificationAudience
{
    /// <summary>Users of the rule's org who opted in to the relevant notification preference.</summary>
    OptedInUsers = 0,

    /// <summary>Members of a specific role in the rule's org (<c>AudienceRef</c> = the role id).</summary>
    Role = 1,
}

/// <summary>
/// The concrete provider behind a channel config (docs/35 §4.2). Wave 1 (Email) needs none; Chat/SMS set this
/// so the adapter knows how to format/authenticate.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum NotificationProviderKind
{
    None = 0,
    Slack = 1,
    Teams = 2,
    Twilio = 3,
    Vonage = 4,
}
