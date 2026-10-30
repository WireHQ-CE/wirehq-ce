namespace WireHQ.Domain.Notifications;

/// <summary>
/// The curated set of <b>critical</b> audit actions an SMS notification rule may target (docs/36-sms-notifications.md
/// §6, Wave 4 Slice C). SMS is deliberately restricted to a small set of rare, security/tenant-danger events — every
/// SMS is billable, intrusive, and disclosure-bearing — so a rule can never fire a text on a high-frequency action.
/// <para>
/// This is the <b>single source of truth</b> for both the server-side rule validator (<see cref="NotificationRule"/>,
/// which rejects any SMS rule pattern that isn't one of these) and the rule editor's critical picker (the Application
/// event-types catalog maps <see cref="All"/> into its response, flagging each entry <c>IsCritical</c>) — so the two
/// can never drift.
/// </para>
/// <para>
/// Entries are <b>literal</b> audit-action names (never <c>prefix.*</c> globs) that are actually EMITTED in a
/// tenant-scoped context, so each routes to the acting org's rules (the notification outbox interceptor only captures
/// audit rows with a non-null <c>OrganizationId</c>). Reconciled to the real emitted actions in Slice C from the
/// approved starter set (docs/36 §6). Tunable by the product owner; keep it small.
/// </para>
/// </summary>
public static class NotificationCriticalEvents
{
    /// <summary>One curated critical event: the literal <paramref name="Action"/> audit-action name plus the display
    /// metadata the rule-editor picker shows.</summary>
    public sealed record CriticalEvent(string Action, string Label, string Group);

    /// <summary>The curated critical actions. Literal names only — each is a genuinely-critical, low-frequency,
    /// org-scoped audit action that actually fires today.</summary>
    public static readonly IReadOnlyList<CriticalEvent> All =
    [
        new("mfa.disabled", "Multi-factor authentication disabled", "Security"),
        new("identity.sso.connection.disabled", "Single sign-on disabled", "Security"),
        new("identity.sso.connection.deleted", "Single sign-on connection removed", "Security"),
        new("identity.ldap.connection.disabled", "Directory (LDAP/AD) sync disabled", "Security"),
        new("policy.access.policy.disabled", "Access policy disabled", "Access"),
        new("policy.access.policy.deleted", "Access policy deleted", "Access"),
        new("orch.agent.revoked", "Deployment agent revoked", "Infrastructure"),
        new("wg.instance.deleted", "WireGuard instance deleted", "Infrastructure"),
    ];

    private static readonly IReadOnlySet<string> ActionSet =
        All.Select(e => e.Action).ToHashSet(StringComparer.Ordinal);

    /// <summary>The literal critical action names — the set an SMS rule pattern must belong to.</summary>
    public static IReadOnlySet<string> Actions => ActionSet;

    /// <summary>True when <paramref name="action"/> is a curated critical action (exact match — a glob never matches,
    /// since every curated entry is literal).</summary>
    public static bool IsCritical(string action) => ActionSet.Contains(action);
}
