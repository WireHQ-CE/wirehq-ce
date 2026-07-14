using FluentAssertions;
using WireHQ.Application.Entitlements;
using WireHQ.Domain.Organizations;
using Xunit;

namespace WireHQ.Application.UnitTests.Entitlements;

/// <summary>The plan catalog is the source of truth for what each edition includes. (docs/commercial.md §3/§4)</summary>
public sealed class PlanCatalogTests
{
    private readonly PlanCatalog _catalog = new();

    [Fact]
    public void Community_excludes_paid_features_and_caps_resources()
    {
        var plan = _catalog.For(OrganizationEdition.Community);

        plan.Has(PlanFeatures.FleetDashboard).Should().BeFalse();
        plan.Has(PlanFeatures.Teams).Should().BeFalse();
        plan.Limit(PlanResource.Instances).Should().Be(3);
        plan.IsUnlimited(PlanResource.Instances).Should().BeFalse();
    }

    [Fact]
    public void Pro_includes_the_operate_a_fleet_features()
    {
        var plan = _catalog.For(OrganizationEdition.Pro);

        plan.Has(PlanFeatures.FleetDashboard).Should().BeTrue();
        plan.Has(PlanFeatures.DriftAutoReconverge).Should().BeTrue();
        plan.Has(PlanFeatures.Teams).Should().BeTrue();
        plan.Has(PlanFeatures.BulkEnrollment).Should().BeTrue();
        plan.Has(PlanFeatures.Sso).Should().BeFalse("SSO is a roadmap feature, not yet built");
        plan.Has(PlanFeatures.Ldap).Should().BeFalse("LDAP/AD directory sync is Enterprise-only, not in Pro");
        plan.Limit(PlanResource.Instances).Should().Be(50);
    }

    [Fact]
    public void Enterprise_includes_the_pro_features_at_unlimited_scale()
    {
        var plan = _catalog.For(OrganizationEdition.Enterprise);

        plan.Has(PlanFeatures.FleetDashboard).Should().BeTrue();
        plan.Has(PlanFeatures.BulkEnrollment).Should().BeTrue();
        // SSO shipped (docs/21 wave 2) and is now an Enterprise capability. The remaining roadmap capabilities
        // are defined (PlanFeatures) but NOT granted by any plan until they ship — the product must not claim
        // what it can't deliver (docs/09-roadmap.md "sold-but-not-built").
        plan.Has(PlanFeatures.Sso).Should().BeTrue("SSO is built and is an Enterprise feature (docs/21)");
        plan.Has(PlanFeatures.Scim).Should().BeTrue("SCIM is built and is an Enterprise feature (docs/21 wave 5)");
        plan.Has(PlanFeatures.AccessPolicies).Should().BeTrue("Access Policies compile + apply is built (docs/22 wave 2)");
        plan.Has(PlanFeatures.Ldap).Should().BeTrue("LDAP/AD directory sync is built and is an Enterprise feature (docs/23 wave 2)");
        plan.Has(PlanFeatures.CustomRoles).Should().BeTrue("custom roles is built and is an Enterprise feature (docs/25)");
        plan.Has(PlanFeatures.ApiKeys).Should().BeTrue("API keys is built and is an Enterprise feature (docs/26)");
        plan.Has(PlanFeatures.NotificationsChat).Should().BeTrue("Chat Alerts is built and is an Enterprise feature (docs/35 Wave 2)");
        plan.IsUnlimited(PlanResource.Instances).Should().BeTrue();
        plan.IsUnlimited(PlanResource.Peers).Should().BeTrue();
    }

    [Fact]
    public void CommunityEdition_is_lean_free_core_but_uncapped()
    {
        // The self-hosted CE base: the SAME empty gated-feature set as SaaS Community (the free core is ungated),
        // but UNCAPPED — premium capability is added by activating Marketplace modules, not by the plan.
        // (docs/29 M-2/M-11)
        var plan = _catalog.For(OrganizationEdition.CommunityEdition);

        plan.Has(PlanFeatures.Teams).Should().BeFalse();
        plan.Has(PlanFeatures.ApiKeys).Should().BeFalse();
        plan.IsUnlimited(PlanResource.Instances).Should().BeTrue("a self-hoster runs their own hardware");
        plan.IsUnlimited(PlanResource.Peers).Should().BeTrue();
        plan.IsUnlimited(PlanResource.Gateways).Should().BeTrue();

        // Audit retention is unlimited on the CE base (a switch arm the review flagged — a missing arm would
        // silently clamp CE to 30 days). (docs/29 M-5 finding)
        _catalog.AuditRetentionWindow(OrganizationEdition.CommunityEdition).Should().BeNull();
    }
}
