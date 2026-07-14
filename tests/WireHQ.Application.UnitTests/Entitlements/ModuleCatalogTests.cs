using FluentAssertions;
using WireHQ.Application.Entitlements;
using Xunit;

namespace WireHQ.Application.UnitTests.Entitlements;

/// <summary>
/// The module catalogue maps a Marketplace slug to the capability it unlocks + whether its code survives the CE
/// strip. A design review corrected the gate-only set (SSO/SCIM/LDAP/Access-Policies are stripped from CE →
/// code-delivered/deferred). (docs/29-ce-marketplace-modules.md §5/M-8)
/// </summary>
public sealed class ModuleCatalogTests
{
    [Theory]
    [InlineData("team-management", PlanFeatures.Teams)]
    [InlineData("custom-roles", PlanFeatures.CustomRoles)]
    [InlineData("api-extensions", PlanFeatures.ApiKeys)]
    [InlineData("audit-export", PlanFeatures.AuditExport)]
    public void Gate_only_modules_are_kept_core_and_map_to_their_feature(string slug, string feature)
    {
        var module = ModuleCatalog.Find(slug);

        module.Should().NotBeNull();
        module!.Delivery.Should().Be(ModuleDelivery.GateOnly);
        module.Features.Should().Contain(feature);
    }

    [Theory]
    [InlineData("saml-authentication")]
    [InlineData("ldap-integration")]
    [InlineData("access-policies")]
    public void Stripped_capabilities_are_code_delivered_not_gate_only(string slug)
    {
        // These would unlock a dead entitlement on CE (their code is stripped), so they must be flagged
        // code-delivered — the activation endpoint refuses those in v1 (docs/29 M-8).
        ModuleCatalog.Find(slug)!.Delivery.Should().Be(ModuleDelivery.CodeDelivered);
    }

    [Fact]
    public void Find_is_case_insensitive_and_returns_null_for_unknown()
    {
        ModuleCatalog.Find("TEAM-MANAGEMENT").Should().NotBeNull();
        ModuleCatalog.Find("no-such-module").Should().BeNull();
    }

    [Fact]
    public void Every_module_maps_to_at_least_one_known_feature_key()
    {
        foreach (var module in ModuleCatalog.Modules)
        {
            module.Features.Should().NotBeEmpty($"module '{module.Slug}' must unlock something");
        }
    }
}
