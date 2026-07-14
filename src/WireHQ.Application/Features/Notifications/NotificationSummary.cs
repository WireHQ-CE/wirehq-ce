using WireHQ.Domain.Auditing;

namespace WireHQ.Application.Features.Notifications;

/// <summary>
/// Builds the <b>minimal, redacted</b> human summary for a notification from an audit event (docs/35-notifications.md
/// §4.6). Deliberately carries only the action, outcome, actor and target — <b>never</b> the raw audit <c>Changes</c>
/// JSON that <c>WebhookPayload</c> embeds: notifications go to humans and shared chat channels, not machine endpoints
/// an admin controls, so leaking a change diff would be an intra-tenant read-authorization bypass.
/// </summary>
public static class NotificationSummary
{
    /// <summary>A rendered notification: a subject and a <b>plain-text</b> redacted body. Channels format it for
    /// their medium (Email → HTML via <see cref="ToHtml"/>; chat → Block Kit; SMS → the plain text).</summary>
    public sealed record Rendered(string Subject, string Summary);

    public static Rendered From(AuditLog audit)
    {
        var actor = string.IsNullOrWhiteSpace(audit.ActorEmail) ? audit.ActorType : audit.ActorEmail;
        var outcome = audit.Outcome.ToString();
        var target = string.IsNullOrWhiteSpace(audit.TargetType)
            ? null
            : $"{audit.TargetType}{(string.IsNullOrWhiteSpace(audit.TargetId) ? string.Empty : $" ({audit.TargetId})")}";

        var subject = $"WireHQ: {audit.Action} ({outcome})";
        var summary = target is null
            ? $"{audit.Action} — {outcome}, by {actor} at {audit.OccurredAtUtc:u}."
            : $"{audit.Action} — {outcome}, by {actor} on {target} at {audit.OccurredAtUtc:u}.";

        return new Rendered(subject, summary);
    }

    /// <summary>A summary for the "send test" path — no real event.</summary>
    public static Rendered Test(string ruleName)
    {
        var subject = "WireHQ: test notification";
        var summary = $"This is a test of your notification rule \"{ruleName}\". If you received it, the rule is wired correctly.";
        return new Rendered(subject, summary);
    }

    /// <summary>Wrap a subject + plain body in minimal, escaped HTML for the Email channel.</summary>
    public static string ToHtml(string subject, string body) =>
        $"<div style=\"font-family:system-ui,sans-serif;font-size:14px;color:#1f2933\">" +
        $"<p style=\"font-weight:600;margin:0 0 8px\">{Escape(subject)}</p>" +
        $"<p style=\"margin:0\">{Escape(body)}</p></div>";

    private static string Escape(string value) => value
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal);
}
