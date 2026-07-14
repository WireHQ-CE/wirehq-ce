using WireHQ.Domain.Common;
using WireHQ.Domain.Webhooks;
using WireHQ.Shared.Results;

namespace WireHQ.Domain.Notifications;

/// <summary>
/// A per-organization <b>notification routing rule</b> (docs/35-notifications.md §4.2): "on audit events matching
/// <see cref="EventPattern"/>, deliver via <see cref="ChannelKind"/> to <see cref="Audience"/>". Events are audit
/// action names (or <c>prefix.*</c> globs — the same catalog webhooks use), matched via the shared
/// <see cref="WebhookEventMatcher"/>. Tenant-owned in the reused <c>identity</c> schema (RLS for free).
/// <para>
/// <b>Gating lives in the command, not here.</b> The rule stores the resolved <see cref="RequiredFeature"/> the
/// command computed (null for a free-core Email rule; a channel/routing feature key otherwise) so the background
/// drain can re-check the live entitlement union per delivery (MM-14) without re-deriving it — the domain stays
/// gating-agnostic (docs/35 §4.4).
/// </para>
/// </summary>
public sealed class NotificationRule : AggregateRoot, ITenantOwned, IAuditable
{
    public const int MaxNameLength = 128;
    public const int MaxPatternLength = 128;

    // EF Core
    private NotificationRule()
    {
    }

    private NotificationRule(
        Guid id, Guid organizationId, string name, string eventPattern, ChannelKind channel,
        NotificationAudience audience, Guid? audienceRef, string? requiredFeature)
        : base(id)
    {
        OrganizationId = organizationId;
        Name = name;
        EventPattern = eventPattern;
        ChannelKind = channel;
        Audience = audience;
        AudienceRef = audienceRef;
        RequiredFeature = requiredFeature;
        Enabled = true;
    }

    public Guid OrganizationId { get; private set; }

    public string Name { get; private set; } = null!;

    /// <summary>The subscribed audit-action pattern (exact name or a <c>prefix.*</c> glob).</summary>
    public string EventPattern { get; private set; } = null!;

    public ChannelKind ChannelKind { get; private set; }

    public NotificationAudience Audience { get; private set; }

    /// <summary>The role id when <see cref="Audience"/> is <see cref="NotificationAudience.Role"/>; else null.</summary>
    public Guid? AudienceRef { get; private set; }

    /// <summary>The entitlement feature key this rule's deliveries must hold to be sent (null = free-core Email).
    /// Set by the command from the channel + free-quota decision; the drain re-checks it (docs/35 §4.4).</summary>
    public string? RequiredFeature { get; private set; }

    public bool Enabled { get; private set; }

    // IAuditable
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public Guid? CreatedBy { get; private set; }
    public DateTimeOffset? UpdatedAtUtc { get; private set; }
    public Guid? UpdatedBy { get; private set; }

    public static Result<NotificationRule> Create(
        Guid organizationId, string name, string eventPattern, ChannelKind channel,
        NotificationAudience audience, Guid? audienceRef, string? requiredFeature)
    {
        if (Validate(name, eventPattern, audience, audienceRef) is { } error)
        {
            return error;
        }

        return new NotificationRule(
            Guid.CreateVersion7(), organizationId, name.Trim(), eventPattern.Trim(), channel, audience, audienceRef, requiredFeature);
    }

    public Result Update(
        string name, string eventPattern, ChannelKind channel,
        NotificationAudience audience, Guid? audienceRef, string? requiredFeature)
    {
        if (Validate(name, eventPattern, audience, audienceRef) is { } error)
        {
            return error;
        }

        Name = name.Trim();
        EventPattern = eventPattern.Trim();
        ChannelKind = channel;
        Audience = audience;
        AudienceRef = audienceRef;
        RequiredFeature = requiredFeature;
        return Result.Success();
    }

    public void Enable() => Enabled = true;

    public void Disable() => Enabled = false;

    /// <summary>True when this rule subscribes to the given audit action (exact or <c>prefix.*</c>).</summary>
    public bool Matches(string action) => Enabled && WebhookEventMatcher.Matches(EventPattern, action);

    private static Error? Validate(string name, string eventPattern, NotificationAudience audience, Guid? audienceRef)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > MaxNameLength)
        {
            return NotificationErrors.InvalidName;
        }

        var pattern = eventPattern?.Trim() ?? string.Empty;
        if (pattern.Length == 0 || pattern.Length > MaxPatternLength)
        {
            return NotificationErrors.InvalidPattern;
        }

        if (audience == NotificationAudience.Role && audienceRef is null)
        {
            return NotificationErrors.MissingRole;
        }

        return null;
    }
}

public static class NotificationErrors
{
    public static readonly Error InvalidName =
        Error.Validation("notification.invalid_name", "Enter a rule name of 128 characters or fewer.");

    public static readonly Error InvalidPattern =
        Error.Validation("notification.invalid_pattern", "Enter an event pattern of 128 characters or fewer.");

    public static readonly Error MissingRole =
        Error.Validation("notification.missing_role", "Choose a role for a role-targeted rule.");

    public static readonly Error NotFound =
        Error.NotFound("notification.not_found", "Notification rule was not found.");

    public static readonly Error ChannelNotAvailable =
        Error.Validation("notification.channel_unavailable", "That notification channel is not available on your plan.");

    public static readonly Error FreeQuotaExceeded =
        Error.Validation("notification.free_quota_exceeded", "You've reached the free notification-rule limit; the Advanced Notifications module lifts it.");
}
