namespace WireHQ.Application.Entitlements;

/// <summary>The intended commercial tier a Marketplace module maps to on WireHQ Cloud (on the Community Edition every
/// module is bought à-la-carte). The authoritative gate is still the feature key in <see cref="PlanCatalog"/> +
/// <see cref="ModuleCatalog"/>; this is presentation/positioning metadata. (docs/33 §7)</summary>
public enum ModuleTier
{
    Pro,
    Enterprise,
}

/// <summary>Whether a module can be activated today. <see cref="ComingSoon"/> = defined in the catalogue for display
/// but not yet activatable on a CE install (the code-delivered set, pending the Architecture-B module runtime — docs/33
/// §12).</summary>
public enum ModuleStatus
{
    Available,
    ComingSoon,
}

/// <summary>
/// Presentation + lifecycle metadata for a Marketplace module — the kept-core manifest that makes an activatable module
/// meet the readiness bar's <b>Docs</b> criterion and reframes version/upgrade honestly (docs/33 §5, ADR-048). It hangs
/// off the same slug as <see cref="ModuleDefinition"/> (the activation source of truth); this carries name, category,
/// version, docs/changelog anchors, min-CE-version, tier and status. NOTE the type is <c>MarketplaceModuleManifest</c>,
/// not <c>ModuleManifest</c> — the latter is the unrelated compile-time IModule registry (docs/33 §0/MM-2).
/// <para><c>Version</c> is the capability's semver as shipped in this CE build — the head of the module's per-module
/// CHANGELOG (a CI check ties the two so it can't rot; docs/33 §5.3). <c>Delivery</c> is the CE delivery mode for a
/// backed module (matches <see cref="ModuleDefinition.Delivery"/>) and is <c>null</c> for a not-yet-built manifest
/// entry with no <see cref="ModuleDefinition"/>.</para>
/// </summary>
public sealed record MarketplaceModuleManifest(
    string Slug,
    string Name,
    string Category,
    string Version,
    string Summary,
    string DocsAnchor,
    string ChangelogAnchor,
    string MinCeVersion,
    ModuleTier Tier,
    ModuleStatus Status,
    ModuleDelivery? Delivery);

/// <summary>
/// The kept-core manifest registry — the single source of presentation/lifecycle truth for the <b>backed</b> Marketplace
/// modules (every <see cref="ModuleCatalog"/> slug has an entry here; validated one-directionally by
/// <c>MarketplaceModuleCatalogTests</c>). The not-yet-built marketing placeholders stay hand-authored in the frontend
/// catalogue until their subsystem ships, at which point they gain a manifest here. Surfaced anonymously by
/// <see cref="PublicMarketplaceModulesQuery"/>. Ships in every edition (kept-core), so both the CE Modules console and
/// the SaaS marketplace pages read the same source. (docs/33 §5.1/§5.2, ADR-048)
/// </summary>
public static class MarketplaceModuleCatalog
{
    // v1 module semver — the head of each module's per-module CHANGELOG; the CI check asserts they agree (docs/33 §5.3).
    private const string V1 = "1.0.0";

    // The CE release these gate-only modules first became activatable (the CE Marketplace launch, v0.40.0).
    private const string CeBaseline = "0.40.0";

    // Chat Alerts (docs/35 Wave 2) first becomes activatable in the release that ships the notification Chat channel.
    private const string NotificationsBaseline = "0.80.0";

    private static string Docs(string slug) => $"docs/marketplace/{slug}/README.md";
    private static string Changelog(string slug) => $"docs/marketplace/{slug}/CHANGELOG.md";

    private static MarketplaceModuleManifest Manifest(
        string slug, string name, string category, string summary,
        ModuleTier tier, ModuleStatus status, ModuleDelivery delivery, string minCe = CeBaseline) =>
        new(slug, name, category, V1, summary, Docs(slug), Changelog(slug), minCe, tier, status, delivery);

    public static readonly IReadOnlyList<MarketplaceModuleManifest> Manifests =
    [
        // --- Live gate-only modules (activatable in the CE today) ---
        Manifest("team-management", "Teams", "Identity & Access",
            "Group members into teams and manage access by team rather than one user at a time.",
            ModuleTier.Pro, ModuleStatus.Available, ModuleDelivery.GateOnly),
        Manifest("fleet-dashboard", "Fleet Dashboard", "Deployment & Fleet",
            "A live overview of every gateway, agent and peer across your fleet.",
            ModuleTier.Pro, ModuleStatus.Available, ModuleDelivery.GateOnly),
        Manifest("auto-reconverge", "Drift Auto-reconverge", "Operations & Resilience",
            "Automatically re-apply configuration when a gateway drifts from its intended state.",
            ModuleTier.Pro, ModuleStatus.Available, ModuleDelivery.GateOnly),
        Manifest("bulk-enrolment", "Bulk Enrolment", "Deployment & Fleet",
            "Onboard many peers at once from a CSV or template.",
            ModuleTier.Pro, ModuleStatus.Available, ModuleDelivery.GateOnly),
        Manifest("custom-roles", "Custom Roles", "Identity & Access",
            "Define your own roles with exactly the permissions you choose, privilege-escalation guarded.",
            ModuleTier.Enterprise, ModuleStatus.Available, ModuleDelivery.GateOnly),
        Manifest("api-extensions", "API Extensions", "Integrations",
            "Scoped, revocable API keys and audit-sourced outbound webhooks for automation.",
            ModuleTier.Enterprise, ModuleStatus.Available, ModuleDelivery.GateOnly),
        Manifest("audit-export", "Audit Export", "Reporting & Analytics",
            "Export your tamper-evident audit log as CSV or JSON for archival and compliance.",
            ModuleTier.Enterprise, ModuleStatus.Available, ModuleDelivery.GateOnly),
        Manifest("customisation-rebranding", "Customisation & Rebranding", "Branding & White-label",
            "Make the instance your own: product name, accent colour and logo/favicon across the whole control plane.",
            ModuleTier.Enterprise, ModuleStatus.Available, ModuleDelivery.GateOnly),
        Manifest("teams-connector", "Chat Alerts (Teams & Slack)", "Notifications",
            "Route notification rules to a Microsoft Teams or Slack channel — one-off, incoming-webhook alerts on the events you choose.",
            ModuleTier.Enterprise, ModuleStatus.Available, ModuleDelivery.GateOnly, minCe: NotificationsBaseline),

        // --- Code-delivered set: shown for display, NOT activatable until the Architecture-B module runtime + the
        //     AGPL review (docs/33 §12/MM-8). Status = ComingSoon; the activation endpoint refuses them. ---
        Manifest("saml-authentication", "SAML Authentication", "Identity & Access",
            "Bring your own identity provider: OIDC + SAML 2.0 single sign-on and SCIM 2.0 provisioning.",
            ModuleTier.Enterprise, ModuleStatus.ComingSoon, ModuleDelivery.CodeDelivered),
        Manifest("ldap-integration", "LDAP Integration", "Identity & Access",
            "Directory sync and bind-at-login against Active Directory or OpenLDAP, with group-to-role mapping.",
            ModuleTier.Enterprise, ModuleStatus.ComingSoon, ModuleDelivery.CodeDelivered),
        Manifest("access-policies", "Access Policies", "Identity & Access",
            "Declarative who-can-reach-what, compiled to per-peer WireGuard AllowedIPs over your deploy pipeline.",
            ModuleTier.Enterprise, ModuleStatus.ComingSoon, ModuleDelivery.CodeDelivered),
    ];

    /// <summary>The manifest for a slug, or null if unknown (case-insensitive).</summary>
    public static MarketplaceModuleManifest? Find(string slug) =>
        Manifests.FirstOrDefault(m => string.Equals(m.Slug, slug, StringComparison.OrdinalIgnoreCase));
}
