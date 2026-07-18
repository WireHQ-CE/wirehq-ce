namespace WireHQ.Application.Entitlements;

/// <summary>
/// How a Marketplace module delivers its capability. <see cref="GateOnly"/> — the capability code already ships
/// in the (kept-core) CE build, so an activated licence merely flips the entitlement on. <see cref="CodeDelivered"/>
/// — the capability code is stripped from the CE (a SaaS-only feature area), so it needs the phase-4 <c>modules/</c>
/// runtime to ship the code as a licensed package; those are deferred and MUST NOT be activatable in v1, or a buyer
/// would unlock a dead entitlement (no controller, no handler, no nav). (docs/29 §5/M-8/M-14)
/// </summary>
public enum ModuleDelivery
{
    GateOnly,
    CodeDelivered,
}

/// <summary>A Marketplace module: its slug, the plan feature key(s) it unlocks, and how it is delivered.</summary>
public sealed record ModuleDefinition(string Slug, IReadOnlySet<string> Features, ModuleDelivery Delivery);

/// <summary>
/// The single source of truth mapping a Marketplace module slug to the capability (feature keys) it unlocks and
/// whether its code survives the CE strip. Shared by SaaS issuance validation and the CE unlock; the frontend
/// catalogue's display strings hang off these slugs. The v1 gate-only set was verified against <c>ce/remove.txt</c>
/// by a design review — SSO/SCIM/LDAP/Access-Policies are stripped from CE and therefore
/// <see cref="ModuleDelivery.CodeDelivered"/> (deferred). (docs/29-ce-marketplace-modules.md §5/M-3/M-8)
/// </summary>
public static class ModuleCatalog
{
    public static readonly IReadOnlyList<ModuleDefinition> Modules =
    [
        // Gate-only — capability code is kept-core in the generated CE, so a licence just flips the entitlement.
        new("team-management", new HashSet<string> { PlanFeatures.Teams }, ModuleDelivery.GateOnly),
        new("fleet-dashboard", new HashSet<string> { PlanFeatures.FleetDashboard }, ModuleDelivery.GateOnly),
        new("auto-reconverge", new HashSet<string> { PlanFeatures.DriftAutoReconverge }, ModuleDelivery.GateOnly),
        new("bulk-enrolment", new HashSet<string> { PlanFeatures.BulkEnrollment }, ModuleDelivery.GateOnly),
        new("custom-roles", new HashSet<string> { PlanFeatures.CustomRoles }, ModuleDelivery.GateOnly),
        // api-extensions is union-aware end-to-end: the API-key auth handler reads base ∪ activated modules
        // (docs/29 M-16, ApiKeyAuthenticationHandler), so a CE org that activates it can mint AND authenticate keys.
        new("api-extensions", new HashSet<string> { PlanFeatures.ApiKeys }, ModuleDelivery.GateOnly),
        // audit-export delivers CSV/JSON + SIEM-format OCSF/CEF on CE — AuditSiemFormatter is kept-core (folded in, docs/33 MM-12).
        new("audit-export", new HashSet<string> { PlanFeatures.AuditExport }, ModuleDelivery.GateOnly),
        // Branding & white-label (docs/34) — install-global, kept-core; a licence lights up the branding.basic gate.
        new("customisation-rebranding", new HashSet<string> { PlanFeatures.Branding }, ModuleDelivery.GateOnly),
        // Chat Alerts — Teams/Slack notification channel (docs/35 Wave 2), kept-core; a licence lights up the
        // notifications.chat gate so an operator can route events to a chat webhook.
        new("teams-connector", new HashSet<string> { PlanFeatures.NotificationsChat }, ModuleDelivery.GateOnly),
        // Advanced Notifications — advanced routing (docs/35 Wave 3), kept-core; a licence lights up the
        // notifications.routing gate. Slice A ships multi-pattern routing rules; later highlights follow.
        new("advanced-notifications", new HashSet<string> { PlanFeatures.NotificationsRouting }, ModuleDelivery.GateOnly),

        // Code-delivered — capability code is STRIPPED from the CE, so these unlock nothing until the phase-4
        // modules/ runtime ships their code (+ the AGPL review). Defined so the catalogue can show them
        // "coming soon"; the activation endpoint refuses them. (docs/29 M-8/M-14)
        new("saml-authentication", new HashSet<string> { PlanFeatures.Sso, PlanFeatures.Scim }, ModuleDelivery.CodeDelivered),
        new("ldap-integration", new HashSet<string> { PlanFeatures.Ldap }, ModuleDelivery.CodeDelivered),
        new("access-policies", new HashSet<string> { PlanFeatures.AccessPolicies }, ModuleDelivery.CodeDelivered),
    ];

    /// <summary>The module a slug names, or null if unknown (case-insensitive).</summary>
    public static ModuleDefinition? Find(string slug) =>
        Modules.FirstOrDefault(m => string.Equals(m.Slug, slug, StringComparison.OrdinalIgnoreCase));
}
