namespace WireHQ.Application.Authorization;

/// <summary>
/// The canonical permission catalog — the single source of truth for permission keys. Lives in
/// Application because use cases declare their <c>RequiredPermissions</c> from it; Identity's
/// policy evaluator and the Infrastructure seeder both read <see cref="All"/> to populate the
/// database and authorization policies. Keys are stable strings; never rename one in place —
/// add a new key and migrate. (docs/04-security.md)
/// </summary>
public static class Permissions
{
    public static class Organization
    {
        public const string Read = "org.read";
        public const string Update = "org.settings.update";
        public const string Delete = "org.delete";
    }

    public static class Users
    {
        public const string Read = "identity.users.read";
        public const string Invite = "identity.users.invite";
        public const string Update = "identity.users.update";
        public const string Remove = "identity.users.remove";
    }

    public static class Teams
    {
        public const string Read = "identity.teams.read";
        public const string Manage = "identity.teams.manage";
    }

    public static class Roles
    {
        public const string Read = "identity.roles.read";
        public const string Manage = "identity.roles.manage";
    }

    public static class Audit
    {
        public const string Read = "audit.logs.read";
    }

    public static class Sso
    {
        // Manage the org's Enterprise SSO connection (config, domains, role mappings). Gated by the
        // identity.sso entitlement at the use-case level; the permission is seeded on every edition (dormant
        // where SSO isn't sold — like the identity.sso plan key). (docs/21-enterprise-identity.md)
        public const string Manage = "identity.sso.manage";
    }

    public static class Ldap
    {
        // View (Read) and manage (Manage) the org's LDAP / Active Directory directory-sync connection. Gated by
        // the identity.ldap entitlement at the use-case level; both permissions seed on every edition (dormant
        // where directory sync isn't sold — like the identity.ldap plan key). These strings are KEPT CORE (not
        // stripped from the CE); the SaaS engine that consumes them lives in Features/Directory + Domain/Directory
        // (stripped), which ships instead as a CE Marketplace module. (docs/23-ldap-directory-sync.md §8)
        public const string Read = "identity.ldap.read";
        public const string Manage = "identity.ldap.manage";
    }

    public static class ApiKeys
    {
        // Manage the org's API keys (create/list/revoke). Gated by the api.keys entitlement at the use-case level;
        // seeded on every edition (dormant where api.keys isn't sold — the CE defaults to Enterprise, so it's live
        // there). Kept core — API keys are an entitlement-gated platform capability, not a CE-stripped module.
        // (docs/26-api-keys-webhooks.md §6)
        public const string Manage = "api.keys.manage";
    }

    public static class AccessPolicy
    {
        // View (Read) and author (Manage) the org's Access Policies — declarative "who can reach what",
        // compiled to WireGuard AllowedIPs. Gated by the policy.access entitlement at the use-case level; both
        // permissions seed on every edition (dormant where Access Policies isn't sold — like the policy.access
        // plan key). These strings are KEPT CORE (not stripped from the CE); the SaaS engine that consumes them
        // lives in Features/Policy + Domain/Policy (stripped). (docs/22-access-policies.md §9)
        public const string Read = "policy.access.read";
        public const string Manage = "policy.access.manage";
    }

    public static class Modules
    {
        // Activate and deactivate CE Marketplace module licences on a self-hosted install (docs/29). Seeded on
        // every edition, but dormant in SaaS — the hosted product unlocks capability through plan bundles and
        // surfaces no module console; only the Community Edition ships the activation console + endpoint. The
        // permission string is KEPT CORE (stable across editions, like the Ldap / AccessPolicy keys); the
        // activation engine (Features/Modules) is CE-only (overlay-added). (docs/29 M-9/M-10)
        public const string Manage = "marketplace.modules.manage";
    }

    public static class Branding
    {
        // Configure the install's branding (product name, accent colour, logo/favicon). Gated ALSO by the
        // branding.basic entitlement at the use-case level, so on SaaS (where no plan grants branding.basic) it is
        // inert, and on the CE it lights up once the branding module is activated. Seeded on every edition; kept core.
        // Branding is install-global, so it is gated on a PERMISSION (not the platform tier — the CE has no platform
        // tier), which the org Owner/Admin holds. (docs/34 §4.2/BR-12)
        public const string Manage = "branding.manage";
    }

    public static class Notifications
    {
        // Manage notification routing rules + channel config (docs/35-notifications.md §4.4). A SENSITIVE permission:
        // a rule confers read of any curated event's detail via routing, so it is NOT a default org-member grant —
        // seeded to Owner/Admin only. Free-core Email rules are permission-only (no feature key); Chat/SMS/advanced
        // rules ALSO require the channel/routing entitlement, enforced in the command. Kept core, every edition.
        public const string Manage = "notifications.manage";
    }

    /// <summary>Full catalog, grouped for the seeder and the admin UI.</summary>
    public static readonly IReadOnlyList<PermissionDefinition> All =
    [
        new(Organization.Read, "Organization", "View organization details"),
        new(Organization.Update, "Organization", "Update organization settings"),
        new(Organization.Delete, "Organization", "Delete the organization"),
        new(Users.Read, "Users", "View users and memberships"),
        new(Users.Invite, "Users", "Invite users to the organization"),
        new(Users.Update, "Users", "Update users and their roles"),
        new(Users.Remove, "Users", "Remove users from the organization"),
        new(Teams.Read, "Teams", "View teams"),
        new(Teams.Manage, "Teams", "Create, update and delete teams"),
        new(Roles.Read, "Roles", "View roles and permissions"),
        new(Roles.Manage, "Roles", "Create and update custom roles"),
        new(Audit.Read, "Audit", "View the audit log"),
        new(Sso.Manage, "Single Sign-On", "Configure single sign-on and directory provisioning"),
        new(Ldap.Read, "Directory Sync", "View the LDAP / Active Directory connection and sync history"),
        new(Ldap.Manage, "Directory Sync", "Configure LDAP / Active Directory directory sync"),
        new(AccessPolicy.Read, "Access Policies", "View access policies and run simulations"),
        new(AccessPolicy.Manage, "Access Policies", "Create, edit and apply access policies"),
        new(ApiKeys.Manage, "API Keys", "Create, list and revoke API keys"),
        new(Modules.Manage, "Marketplace", "Activate and deactivate module licences"),
        new(Branding.Manage, "Branding", "Configure the install's branding (name, colour, logo)"),
        new(Notifications.Manage, "Notifications", "Configure notification rules and channels"),
    ];
}

public sealed record PermissionDefinition(string Key, string Group, string Description);
