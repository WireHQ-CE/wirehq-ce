using FluentAssertions;
using WireHQ.Application.Entitlements;
using WireHQ.Domain.Organizations;
using Xunit;

namespace WireHQ.Application.UnitTests.Entitlements;

/// <summary>
/// The shared base ∪ active-module union (docs/29 M-4/M-16), the one owner of the effective feature set used by
/// both <see cref="EntitlementService"/> (active tenant) and the anonymous API-key auth handler (a specific
/// key-owning org). Kept-core so the main CI covers it (M-17).
/// </summary>
public sealed class EffectiveFeatureSetTests
{
    private sealed class FakeModules(params string[] features) : IActivatedModuleReader
    {
        private readonly IReadOnlySet<string> _features = new HashSet<string>(features);
        public Task<IReadOnlySet<string>> ActiveFeatureKeysAsync(CancellationToken ct) => Task.FromResult(_features);
    }

    private static EffectiveFeatureSet Build(params string[] moduleFeatures) =>
        new(new PlanCatalog(), new FakeModules(moduleFeatures));

    [Fact]
    public async Task Base_plan_feature_is_included_without_touching_the_module_reader()
    {
        // Enterprise bundles api.keys in its base plan; HasFeature short-circuits before the reader.
        var effective = new EffectiveFeatureSet(new PlanCatalog(), new ThrowingModules());

        (await effective.HasFeatureAsync(OrganizationEdition.Enterprise, PlanFeatures.ApiKeys, default)).Should().BeTrue();
    }

    private sealed class ThrowingModules : IActivatedModuleReader
    {
        public Task<IReadOnlySet<string>> ActiveFeatureKeysAsync(CancellationToken ct) =>
            throw new InvalidOperationException("the reader must not be consulted when the base plan already grants the feature");
    }

    [Fact]
    public async Task CommunityEdition_without_modules_does_not_have_a_paid_feature()
    {
        (await Build().HasFeatureAsync(OrganizationEdition.CommunityEdition, PlanFeatures.ApiKeys, default))
            .Should().BeFalse("the lean CE base has an empty gated-feature set");
    }

    [Fact]
    public async Task An_activated_module_lights_up_the_feature_for_CommunityEdition()
    {
        var effective = Build(PlanFeatures.ApiKeys);

        (await effective.HasFeatureAsync(OrganizationEdition.CommunityEdition, PlanFeatures.ApiKeys, default))
            .Should().BeTrue("the api-extensions module unions api.keys onto the CE base (M-16 makes the key usable)");
    }

    [Fact]
    public async Task Resolve_returns_the_base_set_by_reference_when_no_modules_are_active()
    {
        var catalog = new PlanCatalog();
        var effective = new EffectiveFeatureSet(catalog, new FakeModules());

        var resolved = await effective.ResolveAsync(OrganizationEdition.CommunityEdition, default);

        // The no-modules path must not allocate — EntitlementService relies on the reference match to reuse the
        // static plan untouched (docs/29 M-4).
        resolved.Should().BeSameAs(catalog.For(OrganizationEdition.CommunityEdition).Features);
    }

    [Fact]
    public async Task Resolve_unions_the_base_and_module_features_when_a_module_is_active()
    {
        var catalog = new PlanCatalog();
        var effective = new EffectiveFeatureSet(catalog, new FakeModules(PlanFeatures.Teams));

        var resolved = await effective.ResolveAsync(OrganizationEdition.CommunityEdition, default);

        resolved.Should().Contain(PlanFeatures.Teams);
        // The static base plan is never mutated — it stays empty.
        catalog.For(OrganizationEdition.CommunityEdition).Features.Should().BeEmpty();
    }
}
