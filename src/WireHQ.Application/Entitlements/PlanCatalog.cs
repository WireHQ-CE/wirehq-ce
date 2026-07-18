using WireHQ.Domain.Organizations;

namespace WireHQ.Application.Entitlements;

/// <summary>
/// Feature keys gated by plan. A use case marks itself <see cref="Common.Messaging.IRequiresFeature"/> with
/// one of these; the plan catalog (below) says which plans include it. Keys mirror the capability, not the
/// implementation, so they stay stable. Roadmap keys are defined now so wiring them later is config-only.
/// (docs/commercial.md §3/§6)
/// </summary>
public static class PlanFeatures
{
    // Shipped capabilities gated today
    public const string FleetDashboard = "fleet.dashboard";
    public const string DriftAutoReconverge = "drift.auto_reconverge";
    public const string Teams = "teams";
    public const string BulkEnrollment = "bulk_enrollment";

    // Roadmap capabilities (defined for the catalog; enforced when the feature ships)
    public const string Sso = "identity.sso";
    public const string Scim = "identity.scim";
    public const string CustomRoles = "rbac.custom_roles";
    public const string AccessPolicies = "policy.access";
    public const string CloudProviders = "providers.cloud";
    public const string AuditExport = "audit.export";
    public const string ApiKeys = "api.keys";

    // LDAP / Active Directory directory sync (docs/23-ldap-directory-sync.md). Defined here in wave 1 but
    // deliberately NOT granted by any plan until the sync engine ships in wave 2 (honest gating — the CRUD
    // backend exists, but the capability isn't usable yet). Slated for Enterprise in SaaS (D-1), and sold as a
    // CE Marketplace module.
    public const string Ldap = "identity.ldap";

    // Branding & white-label (docs/34). An install-global capability — sold as a CE Marketplace module (GateOnly) and
    // NOT granted by any base plan (install-global branding rebrands the whole install, so it isn't a per-org SaaS
    // plan feature — docs/34 BR-12). Its code is kept-core, so activating the module lights it up.
    public const string Branding = "branding.basic";

    // Notifications — Chat Alerts (Teams/Slack), docs/35 Wave 2. The free-core Email channel is ungated; a rule that
    // targets a Chat channel requires this key (channel-includes-its-rules, docs/35 N-5). Granted to Enterprise in
    // SaaS; sold à-la-carte as the `teams-connector` CE Marketplace module (GateOnly — kept-core, activation lights
    // it up).
    public const string NotificationsChat = "notifications.chat";

    // Notifications — Advanced routing (docs/35 Wave 3). Advanced rule shapes — multi-pattern rules, digests,
    // quiet-hours, escalation chains, and email beyond the free quota — require this key; a single Email rule on a
    // curated event stays free-core (N-4). This is NOT a prerequisite for Chat/SMS (N-5 — routing is advanced-only).
    // Granted to Pro AND Enterprise (Pro+; editions do not inherit, so both list it); sold à-la-carte as the
    // `advanced-notifications` CE Marketplace module (GateOnly — kept-core, activation lights it up).
    // notifications.sms stays reserved for Wave 4.
    public const string NotificationsRouting = "notifications.routing";
}

/// <summary>A countable resource a plan caps. <see cref="PlanDefinition.Unlimited"/> means no cap.</summary>
public enum PlanResource
{
    Instances,
    Peers,
    Gateways,
    Seats,
    Networks,
}

/// <summary>What a plan includes: a feature set + numeric quotas. (docs/commercial.md §3/§4)</summary>
public sealed record PlanDefinition(IReadOnlySet<string> Features, IReadOnlyDictionary<PlanResource, int> Limits)
{
    public const int Unlimited = -1;

    public bool Has(string feature) => Features.Contains(feature);

    /// <summary>The cap for a resource (<see cref="Unlimited"/> = uncapped). Missing ⇒ unlimited.</summary>
    public int Limit(PlanResource resource) => Limits.TryGetValue(resource, out var v) ? v : Unlimited;

    public bool IsUnlimited(PlanResource resource) => Limit(resource) == Unlimited;
}

/// <summary>
/// Operator knob for the edition a new organisation is provisioned with. Defaults to
/// <see cref="OrganizationEdition.Community"/> (the right production default — self-serve signups start free);
/// self-host operators (and the test/dev environments) can override via <c>Entitlements:DefaultEdition</c>.
/// </summary>
public sealed class EntitlementOptions
{
    public OrganizationEdition DefaultEdition { get; init; } = OrganizationEdition.Community;
}

/// <summary>Resolves a plan (edition) to the features + limits it includes.</summary>
public interface IPlanCatalog
{
    PlanDefinition For(OrganizationEdition edition);

    /// <summary>
    /// How far back a tenant on this edition can read/export its own audit history — the customer-visible
    /// retention window (<c>null</c> = unlimited, i.e. up to the platform's physical retention ceiling). This
    /// is a read-side visibility clamp; physical retention is the sweeper's ceiling. (docs/15 §5)
    /// </summary>
    TimeSpan? AuditRetentionWindow(OrganizationEdition edition);
}

/// <summary>
/// Code-defined plan catalog (mirrors <c>SystemRoles.Definitions</c>) — the single source of truth for what
/// each edition includes. Tunable later behind this seam (e.g. DB-backed) without touching callers.
/// (docs/commercial.md §3/§4/§6)
/// </summary>
public sealed class PlanCatalog : IPlanCatalog
{
    private static readonly PlanDefinition Community = new(
        Features: new HashSet<string>(),
        Limits: new Dictionary<PlanResource, int>
        {
            [PlanResource.Instances] = 3,
            [PlanResource.Peers] = 25,
            [PlanResource.Gateways] = 1,
            [PlanResource.Seats] = 1,
            [PlanResource.Networks] = 2,
        });

    // The self-hosted Community EDITION base — the SAME lean free-core (empty gated-feature set; the free core is
    // ungated) as SaaS Community, but UNCAPPED (a self-hoster runs their own hardware). Premium capability is added
    // by activating Marketplace module licences, which the EntitlementService unions on top. (docs/29 M-2/M-11)
    private static readonly PlanDefinition CommunityEdition = new(
        Features: new HashSet<string>(),
        Limits: new Dictionary<PlanResource, int>());

    private static readonly PlanDefinition Pro = new(
        Features: new HashSet<string>
        {
            PlanFeatures.FleetDashboard, PlanFeatures.DriftAutoReconverge, PlanFeatures.Teams, PlanFeatures.BulkEnrollment,
            PlanFeatures.NotificationsRouting,
        },
        Limits: new Dictionary<PlanResource, int>
        {
            [PlanResource.Instances] = 50,
            [PlanResource.Peers] = 1_000,
            [PlanResource.Gateways] = 25,
            [PlanResource.Seats] = 10,
            [PlanResource.Networks] = 25,
        });

    // Enterprise today = the full Pro feature set at unlimited scale, plus a sales relationship, plus the
    // Enterprise-only capabilities shipped so far: audit export (CSV/JSON; docs/15 §5/§11), Single Sign-On +
    // SCIM (identity.sso/identity.scim; docs/21), Access Policies (policy.access; docs/22), LDAP / Active
    // Directory directory sync + authentication (identity.ldap; docs/23/24), custom roles (rbac.custom_roles;
    // docs/25), and now API keys + webhooks (api.keys; docs/26 — a scoped-key auth path + audit-sourced webhooks
    // on kept-core seams, so a CE self-hoster who activates the api-extensions Marketplace module unlocks them too,
    // docs/29 M-16). The remaining
    // roadmap capability (cloud providers) is still deliberately NOT granted by any plan — its key stays defined in
    // PlanFeatures so wiring it is config-only once built, but no plan claims it until it ships. This keeps the
    // product honest (docs/09-roadmap.md "sold-but-not-built"; docs/commercial.md §3/§6).
    private static readonly PlanDefinition Enterprise = new(
        Features: new HashSet<string>
        {
            PlanFeatures.FleetDashboard, PlanFeatures.DriftAutoReconverge, PlanFeatures.Teams, PlanFeatures.BulkEnrollment,
            PlanFeatures.AuditExport, PlanFeatures.Sso, PlanFeatures.Scim, PlanFeatures.AccessPolicies, PlanFeatures.Ldap,
            PlanFeatures.CustomRoles, PlanFeatures.ApiKeys, PlanFeatures.NotificationsChat, PlanFeatures.NotificationsRouting,
        },
        Limits: new Dictionary<PlanResource, int>());

    public PlanDefinition For(OrganizationEdition edition) => edition switch
    {
        OrganizationEdition.Community => Community,
        OrganizationEdition.CommunityEdition => CommunityEdition,
        OrganizationEdition.Pro => Pro,
        OrganizationEdition.Enterprise => Enterprise,
        _ => Community,
    };

    public TimeSpan? AuditRetentionWindow(OrganizationEdition edition) => edition switch
    {
        OrganizationEdition.Community => TimeSpan.FromDays(30),
        // Self-hosted CE: unlimited read window (bounded only by the operator's own physical retention).
        OrganizationEdition.CommunityEdition => null,
        OrganizationEdition.Pro => TimeSpan.FromDays(365),
        OrganizationEdition.Enterprise => null, // unlimited — bounded only by the physical retention ceiling
        _ => TimeSpan.FromDays(30),
    };
}
