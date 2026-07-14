using FluentAssertions;
using WireHQ.Application.Entitlements;
using WireHQ.Domain.Modules;
using Xunit;

namespace WireHQ.Application.UnitTests.Entitlements;

/// <summary>
/// The pure CE-unlock rules (docs/29 M-4/M-7/M-8): which feature keys a set of stored activated module licences
/// grants right now. Kept-core + unit-tested (M-17) — the CE persistence adapter only maps rows onto
/// <see cref="ActivatedModuleRecord"/> and delegates here, so this is where the revoke / grace / gate-only logic
/// is verified.
/// </summary>
public sealed class ActivatedModuleEvaluatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 11, 12, 0, 0, TimeSpan.Zero);

    private static ActivatedModuleRecord Licence(string slug, ModuleLicenceStatus status = ModuleLicenceStatus.Active, DateTimeOffset? graceEnds = null) =>
        new(slug, status, graceEnds);

    [Fact]
    public void No_licences_grants_nothing()
    {
        ActivatedModuleEvaluator.ActiveFeatureKeys([], Now).Should().BeEmpty();
    }

    [Fact]
    public void An_active_gate_only_licence_with_no_grace_boundary_grants_its_feature()
    {
        // Wave-2 local activation: no online verification yet, so GraceEndsUtc is null → still valid.
        var keys = ActivatedModuleEvaluator.ActiveFeatureKeys([Licence("team-management")], Now);

        keys.Should().ContainSingle().Which.Should().Be(PlanFeatures.Teams);
    }

    [Fact]
    public void An_active_licence_still_in_grace_grants_its_feature()
    {
        var keys = ActivatedModuleEvaluator.ActiveFeatureKeys(
            [Licence("api-extensions", graceEnds: Now.AddDays(10))], Now);

        keys.Should().Contain(PlanFeatures.ApiKeys);
    }

    [Fact]
    public void A_revoked_licence_grants_nothing_even_within_grace()
    {
        // Revocation is authoritative (M-7): a still-in-grace token that the verify loop marked revoked locks.
        var keys = ActivatedModuleEvaluator.ActiveFeatureKeys(
            [Licence("api-extensions", ModuleLicenceStatus.Revoked, graceEnds: Now.AddDays(10))], Now);

        keys.Should().BeEmpty();
    }

    [Fact]
    public void A_lapsed_licence_past_its_grace_window_grants_nothing()
    {
        var keys = ActivatedModuleEvaluator.ActiveFeatureKeys(
            [Licence("custom-roles", graceEnds: Now.AddSeconds(-1))], Now);

        keys.Should().BeEmpty("the offline grace window has elapsed — the capability locks (nag-don't-kill)");
    }

    [Fact]
    public void The_grace_boundary_instant_itself_is_already_expired()
    {
        var keys = ActivatedModuleEvaluator.ActiveFeatureKeys(
            [Licence("custom-roles", graceEnds: Now)], Now);

        keys.Should().BeEmpty();
    }

    [Fact]
    public void An_unknown_module_slug_grants_nothing()
    {
        ActivatedModuleEvaluator.ActiveFeatureKeys([Licence("no-such-module")], Now).Should().BeEmpty();
    }

    [Fact]
    public void A_code_delivered_module_grants_nothing_even_if_activated()
    {
        // Defence-in-depth behind the activation endpoint's refusal (M-8): the capability code is stripped from
        // the CE, so unlocking its entitlement would light a dead feature.
        var keys = ActivatedModuleEvaluator.ActiveFeatureKeys([Licence("saml-authentication")], Now);

        keys.Should().BeEmpty();
    }

    [Fact]
    public void Multiple_active_modules_union_their_features()
    {
        var keys = ActivatedModuleEvaluator.ActiveFeatureKeys(
            [Licence("team-management"), Licence("custom-roles"), Licence("audit-export")], Now);

        keys.Should().BeEquivalentTo([PlanFeatures.Teams, PlanFeatures.CustomRoles, PlanFeatures.AuditExport]);
    }

    [Fact]
    public void A_valid_licence_alongside_a_revoked_one_grants_only_the_valid_feature()
    {
        var keys = ActivatedModuleEvaluator.ActiveFeatureKeys(
        [
            Licence("team-management"),
            Licence("custom-roles", ModuleLicenceStatus.Revoked),
        ], Now);

        keys.Should().BeEquivalentTo([PlanFeatures.Teams]);
    }
}
