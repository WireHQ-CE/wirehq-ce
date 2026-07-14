using FluentAssertions;
using NSubstitute;
using WireHQ.Application.Abstractions;
using WireHQ.Application.Abstractions.Persistence;
using WireHQ.Application.Entitlements;
using Xunit;

namespace WireHQ.Application.UnitTests.Entitlements;

/// <summary>
/// The entitlement union (docs/29 M-4): the effective plan = the edition's base features UNION the features
/// unlocked by valid activated Marketplace modules. Exercised with no active org (edition falls back to
/// Community, so no DB read is needed), which lets a pure unit test prove the union + its no-op behaviour — and
/// keeps this kept-core seam covered by the main CI (M-17).
/// </summary>
public sealed class EntitlementUnionTests
{
    private static EntitlementService Build(IActivatedModuleReader modules)
    {
        var tenant = Substitute.For<ITenantContext>();
        tenant.OrganizationId.Returns((Guid?)null); // no org → Community base, no DB access
        var catalog = new PlanCatalog();
        // EntitlementService now sources the union from the shared IEffectiveFeatureSet (docs/29 M-16); wire the
        // real helper over the fake reader so this proves the whole seam, not just the service.
        var effective = new EffectiveFeatureSet(catalog, modules);
        return new EntitlementService(Substitute.For<IApplicationDbContext>(), tenant, catalog, effective);
    }

    private sealed class FakeModules(params string[] features) : IActivatedModuleReader
    {
        private readonly IReadOnlySet<string> _features = new HashSet<string>(features);
        public Task<IReadOnlySet<string>> ActiveFeatureKeysAsync(CancellationToken ct) => Task.FromResult(_features);
    }

    [Fact]
    public async Task No_activated_modules_is_a_no_op_over_the_base_plan()
    {
        var service = Build(new NoActivatedModules());

        (await service.HasFeatureAsync(PlanFeatures.Teams, default)).Should().BeFalse();
        (await service.SnapshotAsync(default)).Features.Should().BeEmpty("Community base has no gated features");
    }

    [Fact]
    public async Task An_activated_module_unlocks_its_feature_on_top_of_the_base_plan()
    {
        var service = Build(new FakeModules(PlanFeatures.Teams, PlanFeatures.ApiKeys));

        (await service.HasFeatureAsync(PlanFeatures.Teams, default)).Should().BeTrue();
        (await service.HasFeatureAsync(PlanFeatures.ApiKeys, default)).Should().BeTrue();
        (await service.HasFeatureAsync(PlanFeatures.Sso, default)).Should().BeFalse("no module granted SSO");

        var snapshot = await service.SnapshotAsync(default);
        snapshot.Features.Should().Contain(new[] { PlanFeatures.Teams, PlanFeatures.ApiKeys });
    }
}
