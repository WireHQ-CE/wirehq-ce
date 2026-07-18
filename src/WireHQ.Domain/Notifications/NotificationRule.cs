using WireHQ.Domain.Common;
using WireHQ.Domain.Webhooks;
using WireHQ.Shared.Results;

namespace WireHQ.Domain.Notifications;

/// <summary>
/// A per-organization <b>notification routing rule</b> (docs/35-notifications.md §4.2): "on audit events matching
/// <see cref="EventPattern"/> (or any of the advanced <see cref="AdditionalPatterns"/>), deliver via
/// <see cref="ChannelKind"/> to <see cref="Audience"/>". Events are audit action names (or <c>prefix.*</c> globs —
/// the same catalog webhooks use), matched via the shared <see cref="WebhookEventMatcher"/>. Tenant-owned in the
/// reused <c>identity</c> schema (RLS for free).
/// <para>
/// <b>Gating lives in the command, not here.</b> The rule stores the resolved <see cref="RequiredFeatures"/> the
/// command computed — the <b>set</b> of entitlement keys ALL of which the rule's deliveries must hold (empty for a
/// free-core Email rule; the channel key and/or <c>notifications.routing</c> otherwise). Set-valued (docs/35 Wave 3):
/// an advanced rule that also targets a Chat channel needs BOTH keys at once, so revoking <i>either</i> module stops
/// it (MM-14). The background drain re-checks the live union per delivery without re-deriving it — the domain stays
/// gating-agnostic (docs/35 §4.4).
/// </para>
/// </summary>
public sealed class NotificationRule : AggregateRoot, ITenantOwned, IAuditable
{
    public const int MaxNameLength = 128;
    public const int MaxPatternLength = 128;

    /// <summary>Cap on the advanced multi-pattern set (the primary <see cref="EventPattern"/> is separate + always
    /// present), so a single rule can't fan into an unbounded cache/match cost.</summary>
    public const int MaxAdditionalPatterns = 24;

    /// <summary>Cap on the escalation chain (docs/35 §5, Wave 3).</summary>
    public const int MaxEscalationSteps = 5;
    public const int MinEscalationDelayMinutes = 1;
    public const int MaxEscalationDelayMinutes = 10080; // 7 days

    private readonly List<NotificationRulePattern> _additionalPatterns = [];
    private readonly List<NotificationEscalationStep> _escalationSteps = [];

    // EF Core
    private NotificationRule()
    {
    }

    private NotificationRule(
        Guid id, Guid organizationId, string name, string eventPattern, ChannelKind channel,
        NotificationAudience audience, Guid? audienceRef, IReadOnlyCollection<string> requiredFeatures,
        DigestCadence digestCadence, DateTimeOffset? nextDigestAtUtc,
        TimeOnly? quietHoursStart, TimeOnly? quietHoursEnd, string? quietHoursTimeZone)
        : base(id)
    {
        OrganizationId = organizationId;
        Name = name;
        EventPattern = eventPattern;
        ChannelKind = channel;
        Audience = audience;
        AudienceRef = audienceRef;
        RequiredFeatures = Normalize(requiredFeatures);
        DigestCadence = digestCadence;
        NextDigestAtUtc = nextDigestAtUtc;
        SetQuietHours(quietHoursStart, quietHoursEnd, quietHoursTimeZone);
        Enabled = true;
    }

    public Guid OrganizationId { get; private set; }

    public string Name { get; private set; } = null!;

    /// <summary>The primary subscribed audit-action pattern (exact name or a <c>prefix.*</c> glob). Always present,
    /// free-core-eligible on its own; <see cref="AdditionalPatterns"/> are the advanced extension.</summary>
    public string EventPattern { get; private set; } = null!;

    public ChannelKind ChannelKind { get; private set; }

    public NotificationAudience Audience { get; private set; }

    /// <summary>The role id when <see cref="Audience"/> is <see cref="NotificationAudience.Role"/>; else null.</summary>
    public Guid? AudienceRef { get; private set; }

    /// <summary>The entitlement feature keys, <b>all</b> of which this rule's deliveries must hold to be sent (empty =
    /// free-core Email). Set by the command from the channel + advanced-shape decision; the drain re-checks the live
    /// union per delivery (docs/35 §4.4). Stored delimited; exposed as a set.</summary>
    public IReadOnlyCollection<string> RequiredFeatures { get; private set; } = Array.Empty<string>();

    /// <summary>How this rule batches its matched events (docs/35 §4.5). <see cref="DigestCadence.Immediate"/> = one
    /// message per event (free-core); Daily/Weekly COALESCE a window into one message (advanced → requires
    /// <c>notifications.routing</c>).</summary>
    public DigestCadence DigestCadence { get; private set; }

    /// <summary>When this digest rule's next flush is due (the next anchor boundary strictly after the last flush /
    /// creation). Null for an <see cref="DigestCadence.Immediate"/> rule (no digest cursor). Set by the command via
    /// <see cref="Domain.Notifications.DigestSchedule"/> and advanced by the drain on each flush.</summary>
    public DateTimeOffset? NextDigestAtUtc { get; private set; }

    /// <summary>Start of the daily <b>quiet-hours</b> window (advanced → <c>notifications.routing</c>, docs/35 §5). Local
    /// to <see cref="QuietHoursTimeZone"/>; null (with the other two) = no quiet hours. During the window a rule's
    /// deliveries are deferred to the window's end, not dropped (enforced at send time by <see cref="QuietHours"/>).</summary>
    public TimeOnly? QuietHoursStart { get; private set; }

    /// <summary>End of the quiet-hours window. An end <b>at or before</b> <see cref="QuietHoursStart"/> spans midnight
    /// (e.g. <c>22:00</c>–<c>07:00</c>).</summary>
    public TimeOnly? QuietHoursEnd { get; private set; }

    /// <summary>IANA time-zone id the quiet window is interpreted in (validated resolvable at save). Null = no window.</summary>
    public string? QuietHoursTimeZone { get; private set; }

    /// <summary>True when a complete, non-zero quiet-hours window is configured.</summary>
    public bool QuietHoursEnabled => QuietHours.IsConfigured(QuietHoursStart, QuietHoursEnd, QuietHoursTimeZone);

    /// <summary>Extra event globs beyond <see cref="EventPattern"/> — an <b>advanced</b> multi-pattern rule
    /// (<c>notifications.routing</c>). NORMAL composite-keyed <c>(RuleId, Pattern)</c> children (the
    /// <see cref="WebhookEventSubscription"/> pattern) — NOT an owned collection, dodging the append-emits-UPDATE
    /// gotcha ([[ef-owned-collection-append-gotcha]]).</summary>
    public IReadOnlyCollection<NotificationRulePattern> AdditionalPatterns => _additionalPatterns;

    /// <summary>The ordered <b>escalation chain</b> (advanced → <c>notifications.routing</c>, docs/35 §5): if the primary
    /// alert is not acknowledged within a step's delay, the drain fires that step (a different channel/audience). NORMAL
    /// surrogate-keyed children — replaced wholesale on update (new ids), dodging the owned-collection append gotcha
    /// ([[ef-owned-collection-append-gotcha]]). <c>StepOrder</c> is assigned by position (0-based, contiguous).</summary>
    public IReadOnlyCollection<NotificationEscalationStep> EscalationSteps => _escalationSteps;

    /// <summary>True when this rule has an escalation chain — an advanced shape requiring <c>notifications.routing</c>.</summary>
    public bool HasEscalation => _escalationSteps.Count > 0;

    public bool Enabled { get; private set; }

    // IAuditable
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public Guid? CreatedBy { get; private set; }
    public DateTimeOffset? UpdatedAtUtc { get; private set; }
    public Guid? UpdatedBy { get; private set; }

    public static Result<NotificationRule> Create(
        Guid organizationId, string name, string eventPattern, ChannelKind channel,
        NotificationAudience audience, Guid? audienceRef, IReadOnlyCollection<string> requiredFeatures,
        IReadOnlyCollection<string>? additionalPatterns = null,
        DigestCadence digestCadence = DigestCadence.Immediate, DateTimeOffset? nextDigestAtUtc = null,
        TimeOnly? quietHoursStart = null, TimeOnly? quietHoursEnd = null, string? quietHoursTimeZone = null,
        IReadOnlyCollection<EscalationStepSpec>? escalationSteps = null)
    {
        if (Validate(name, eventPattern, audience, audienceRef, additionalPatterns,
                quietHoursStart, quietHoursEnd, quietHoursTimeZone, digestCadence, escalationSteps) is { } error)
        {
            return error;
        }

        var rule = new NotificationRule(
            Guid.CreateVersion7(), organizationId, name.Trim(), eventPattern.Trim(), channel, audience, audienceRef,
            requiredFeatures, digestCadence, nextDigestAtUtc, quietHoursStart, quietHoursEnd, quietHoursTimeZone);
        rule.ReplaceAdditionalPatterns(additionalPatterns ?? Array.Empty<string>());
        rule.ReplaceEscalationSteps(escalationSteps ?? Array.Empty<EscalationStepSpec>());
        return rule;
    }

    public Result Update(
        string name, string eventPattern, ChannelKind channel,
        NotificationAudience audience, Guid? audienceRef, IReadOnlyCollection<string> requiredFeatures,
        IReadOnlyCollection<string>? additionalPatterns = null,
        DigestCadence digestCadence = DigestCadence.Immediate, DateTimeOffset? nextDigestAtUtc = null,
        TimeOnly? quietHoursStart = null, TimeOnly? quietHoursEnd = null, string? quietHoursTimeZone = null,
        IReadOnlyCollection<EscalationStepSpec>? escalationSteps = null)
    {
        if (Validate(name, eventPattern, audience, audienceRef, additionalPatterns,
                quietHoursStart, quietHoursEnd, quietHoursTimeZone, digestCadence, escalationSteps) is { } error)
        {
            return error;
        }

        Name = name.Trim();
        EventPattern = eventPattern.Trim();
        ChannelKind = channel;
        Audience = audience;
        AudienceRef = audienceRef;
        RequiredFeatures = Normalize(requiredFeatures);
        DigestCadence = digestCadence;
        NextDigestAtUtc = nextDigestAtUtc;
        SetQuietHours(quietHoursStart, quietHoursEnd, quietHoursTimeZone);
        ReplaceAdditionalPatterns(additionalPatterns ?? Array.Empty<string>());
        ReplaceEscalationSteps(escalationSteps ?? Array.Empty<EscalationStepSpec>());
        return Result.Success();
    }

    /// <summary>Advance the digest flush cursor after a flush (docs/35 §4.5). Called by the drain with the next anchor
    /// boundary strictly after now — <b>always</b>, even when the window gathered zero jobs, so the cursor never stays
    /// <c>&lt;= now</c> and re-fires forever (finding cursor-advance).</summary>
    public void AdvanceDigestCursor(DateTimeOffset? nextDigestAtUtc) => NextDigestAtUtc = nextDigestAtUtc;

    public void Enable() => Enabled = true;

    public void Disable() => Enabled = false;

    /// <summary>Every event glob this rule subscribes to — the primary plus any advanced additional patterns.</summary>
    public IEnumerable<string> AllPatterns => _additionalPatterns.Select(p => p.Pattern).Prepend(EventPattern);

    /// <summary>True when this rule subscribes to the given audit action (via the primary OR any additional glob).</summary>
    public bool Matches(string action) =>
        Enabled && (WebhookEventMatcher.Matches(EventPattern, action)
                    || _additionalPatterns.Any(p => WebhookEventMatcher.Matches(p.Pattern, action)));

    /// <summary>True when this rule is an <b>advanced</b> shape — it has additional patterns (multi-pattern), a
    /// non-immediate <see cref="DigestCadence"/> (digests), a <see cref="QuietHoursEnabled">quiet-hours window</see>, OR
    /// an <see cref="HasEscalation">escalation chain</see> — the marker the command folds into
    /// <see cref="RequiredFeatures"/> as <c>notifications.routing</c>.</summary>
    public bool IsAdvanced =>
        _additionalPatterns.Count > 0 || DigestCadence != DigestCadence.Immediate || QuietHoursEnabled || HasEscalation;

    private void ReplaceAdditionalPatterns(IReadOnlyCollection<string> patterns)
    {
        // Diff against the current set rather than clear-and-re-add: normal composite-keyed (RuleId, Pattern)
        // children, so on the tracked update path a Clear() + re-add of a retained pattern marks a Deleted and an
        // Added row with the same key → EF tracking conflict at SaveChanges. Remove only the unwanted, add only the
        // missing (the WebhookEndpoint.ReplaceEventTypes lesson). Drop anything equal to the primary EventPattern.
        var desired = patterns.Select(p => p.Trim())
            .Where(p => p.Length > 0 && !string.Equals(p, EventPattern, StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);
        _additionalPatterns.RemoveAll(p => !desired.Contains(p.Pattern));
        var existing = _additionalPatterns.Select(p => p.Pattern).ToHashSet(StringComparer.Ordinal);
        foreach (var pattern in desired)
        {
            if (existing.Add(pattern))
            {
                _additionalPatterns.Add(new NotificationRulePattern(Id, pattern));
            }
        }
    }

    private static IReadOnlyCollection<string> Normalize(IReadOnlyCollection<string> requiredFeatures) =>
        requiredFeatures
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Select(f => f.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    private static Error? Validate(
        string name, string eventPattern, NotificationAudience audience, Guid? audienceRef,
        IReadOnlyCollection<string>? additionalPatterns,
        TimeOnly? quietHoursStart, TimeOnly? quietHoursEnd, string? quietHoursTimeZone,
        DigestCadence digestCadence, IReadOnlyCollection<EscalationStepSpec>? escalationSteps)
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

        if (additionalPatterns is not null)
        {
            var extras = additionalPatterns.Select(p => p?.Trim() ?? string.Empty).Where(p => p.Length > 0).ToList();
            if (extras.Any(p => p.Length > MaxPatternLength))
            {
                return NotificationErrors.InvalidPattern;
            }

            if (extras.Distinct(StringComparer.Ordinal).Count() > MaxAdditionalPatterns)
            {
                return NotificationErrors.TooManyPatterns;
            }
        }

        if (audience == NotificationAudience.Role && audienceRef is null)
        {
            return NotificationErrors.MissingRole;
        }

        // Quiet hours are all-or-none: a start, an end, and a resolvable IANA time zone together, or none at all. A
        // zero-length window (start == end) is meaningless — disable it instead. Validated identically in Create/Update.
        var anyQuiet = quietHoursStart is not null || quietHoursEnd is not null || !string.IsNullOrWhiteSpace(quietHoursTimeZone);
        if (anyQuiet && (quietHoursStart is not { } qs || quietHoursEnd is not { } qe
                         || qs == qe || !QuietHours.IsValidTimeZone(quietHoursTimeZone)))
        {
            return NotificationErrors.InvalidQuietHours;
        }

        // Escalation chain (docs/35 §5, Wave 3): bounded; each step needs a valid audience (a role id for Role) and a
        // sane delay. Escalation is time-critical + per-event, so it is INCOMPATIBLE with digest coalescing — a digest
        // has no single owning job to escalate (pre-code review B3/B5/B8). StepOrder is assigned by position, so the
        // chain is contiguous by construction (no contiguity check needed).
        if (escalationSteps is { Count: > 0 })
        {
            if (digestCadence != DigestCadence.Immediate)
            {
                return NotificationErrors.EscalationRequiresImmediate;
            }

            if (escalationSteps.Count > MaxEscalationSteps)
            {
                return NotificationErrors.TooManyEscalationSteps;
            }

            foreach (var step in escalationSteps)
            {
                if (step.DelayMinutes < MinEscalationDelayMinutes || step.DelayMinutes > MaxEscalationDelayMinutes
                    || (step.Audience == NotificationAudience.Role && step.AudienceRef is null))
                {
                    return NotificationErrors.InvalidEscalationStep;
                }
            }
        }

        return null;
    }

    /// <summary>Replace the escalation chain wholesale. Surrogate-keyed children get NEW ids on each rebuild, so a
    /// <c>Clear()</c> + re-add is DELETE-old + INSERT-new (distinct rows) — no same-key Deleted+Added tracking conflict
    /// (unlike the composite-keyed patterns). <c>StepOrder</c> is assigned by position → contiguous 0..n-1 (M4).</summary>
    private void ReplaceEscalationSteps(IReadOnlyCollection<EscalationStepSpec> steps)
    {
        _escalationSteps.Clear();
        var order = 0;
        foreach (var step in steps)
        {
            _escalationSteps.Add(new NotificationEscalationStep(
                Guid.CreateVersion7(), Id, order++, step.DelayMinutes, step.ChannelKind, step.Audience, step.AudienceRef));
        }
    }

    /// <summary>Normalise + store the quiet-hours window: keep all three together, or clear all three (so a partial
    /// window can never linger). The trio is validated before this is reached (Create/Update → <see cref="Validate"/>).</summary>
    private void SetQuietHours(TimeOnly? start, TimeOnly? end, string? timeZoneId)
    {
        var complete = QuietHours.IsConfigured(start, end, timeZoneId);
        QuietHoursStart = complete ? start : null;
        QuietHoursEnd = complete ? end : null;
        QuietHoursTimeZone = complete ? timeZoneId!.Trim() : null;
    }
}

/// <summary>One additional event glob on a multi-pattern <see cref="NotificationRule"/> (advanced routing,
/// <c>notifications.routing</c>). A NORMAL child entity keyed by <c>(RuleId, Pattern)</c> — the
/// <see cref="WebhookEventSubscription"/> pattern, dodging the owned-collection append gotcha
/// ([[ef-owned-collection-append-gotcha]]).</summary>
public sealed class NotificationRulePattern
{
    // EF Core
    private NotificationRulePattern()
    {
    }

    public NotificationRulePattern(Guid ruleId, string pattern)
    {
        RuleId = ruleId;
        Pattern = pattern;
    }

    public Guid RuleId { get; private set; }

    public string Pattern { get; private set; } = null!;
}

/// <summary>One step of a rule's <b>escalation chain</b> (advanced routing, <c>notifications.routing</c>, docs/35 §5). A
/// NORMAL surrogate-keyed child (so the rule can replace the whole chain wholesale without a same-key tracking conflict).
/// Fires <see cref="DelayMinutes"/> after the prior level if the alert is still unacknowledged, to its OWN
/// <see cref="ChannelKind"/>/<see cref="Audience"/> (distinct from the primary's).</summary>
public sealed class NotificationEscalationStep : Entity
{
    // EF Core
    private NotificationEscalationStep()
    {
    }

    public NotificationEscalationStep(
        Guid id, Guid ruleId, int stepOrder, int delayMinutes, ChannelKind channel,
        NotificationAudience audience, Guid? audienceRef)
        : base(id)
    {
        RuleId = ruleId;
        StepOrder = stepOrder;
        DelayMinutes = delayMinutes;
        ChannelKind = channel;
        Audience = audience;
        AudienceRef = audienceRef;
    }

    public Guid RuleId { get; private set; }

    /// <summary>0-based position in the chain — contiguous, assigned by the rule at save.</summary>
    public int StepOrder { get; private set; }

    /// <summary>Minutes after the PRIOR level fired before this step fires, if still unacknowledged.</summary>
    public int DelayMinutes { get; private set; }

    public ChannelKind ChannelKind { get; private set; }

    public NotificationAudience Audience { get; private set; }

    public Guid? AudienceRef { get; private set; }
}

/// <summary>The input shape for one escalation step (docs/35 §5) — the rule assigns <c>StepOrder</c> by position.</summary>
public sealed record EscalationStepSpec(int DelayMinutes, ChannelKind ChannelKind, NotificationAudience Audience, Guid? AudienceRef);

public static class NotificationErrors
{
    public static readonly Error InvalidName =
        Error.Validation("notification.invalid_name", "Enter a rule name of 128 characters or fewer.");

    public static readonly Error InvalidPattern =
        Error.Validation("notification.invalid_pattern", "Enter an event pattern of 128 characters or fewer.");

    public static readonly Error TooManyPatterns =
        Error.Validation("notification.too_many_patterns", "A rule matches at most 24 additional event patterns.");

    public static readonly Error MissingRole =
        Error.Validation("notification.missing_role", "Choose a role for a role-targeted rule.");

    public static readonly Error NotFound =
        Error.NotFound("notification.not_found", "Notification rule was not found.");

    public static readonly Error ChannelNotAvailable =
        Error.Validation("notification.channel_unavailable", "That notification channel is not available on your plan.");

    public static readonly Error FreeQuotaExceeded =
        Error.Validation("notification.free_quota_exceeded", "You've reached the free notification-rule limit; the Advanced Notifications module lifts it.");

    public static readonly Error AdvancedRequired =
        Error.Validation("notification.advanced_required", "That's an advanced routing rule; activate the Advanced Notifications module to use it.");

    public static readonly Error InvalidQuietHours =
        Error.Validation("notification.invalid_quiet_hours", "Set a start time, a different end time, and a valid time zone for quiet hours — or leave all three empty.");

    public static readonly Error EscalationRequiresImmediate =
        Error.Validation("notification.escalation_requires_immediate", "Escalation can't be combined with a daily/weekly digest — use immediate delivery.");

    public static readonly Error TooManyEscalationSteps =
        Error.Validation("notification.too_many_escalation_steps", "An escalation chain has at most 5 steps.");

    public static readonly Error InvalidEscalationStep =
        Error.Validation("notification.invalid_escalation_step", "Each escalation step needs a delay (1–10080 minutes) and, for a role audience, a role.");

    public static readonly Error EscalationChannelUnavailable =
        Error.Validation("notification.escalation_channel_unavailable", "An escalation step uses a channel your plan doesn't include.");

    public static readonly Error AlertNotFound =
        Error.NotFound("notification.alert_not_found", "Alert was not found.");
}
