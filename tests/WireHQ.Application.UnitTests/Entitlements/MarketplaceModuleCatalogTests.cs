using FluentAssertions;
using WireHQ.Application.Entitlements;
using Xunit;

namespace WireHQ.Application.UnitTests.Entitlements;

/// <summary>
/// The Marketplace module manifest is the kept-core presentation/lifecycle source of truth (docs/33 §5.1, ADR-048).
/// Validation is one-directional: every activatable <see cref="ModuleCatalog"/> slug must have a manifest, and a
/// manifest that carries a delivery mode must agree with the catalogue — but a manifest may additionally carry
/// not-yet-built "coming soon" entries with no <see cref="ModuleDefinition"/>. A code-review guard also keeps the
/// Version field honest (== the head of the per-module changelog) once those changelogs land.
/// </summary>
public sealed class MarketplaceModuleCatalogTests
{
    [Fact]
    public void Every_module_catalog_slug_has_a_manifest()
    {
        foreach (var module in ModuleCatalog.Modules)
        {
            MarketplaceModuleCatalog.Find(module.Slug)
                .Should().NotBeNull($"the backed module '{module.Slug}' must carry a manifest (docs/33 §5.1)");
        }
    }

    [Fact]
    public void Manifest_delivery_agrees_with_the_module_catalog()
    {
        foreach (var manifest in MarketplaceModuleCatalog.Manifests)
        {
            var backing = ModuleCatalog.Find(manifest.Slug);
            if (manifest.Delivery is null)
            {
                // A not-yet-built manifest entry (no ModuleDefinition) is allowed (one-directional rule).
                continue;
            }

            backing.Should().NotBeNull($"manifest '{manifest.Slug}' declares a delivery, so it must be a backed module");
            manifest.Delivery.Should().Be(backing!.Delivery,
                $"manifest '{manifest.Slug}' delivery must match its ModuleCatalog entry");
        }
    }

    [Fact]
    public void Manifest_slugs_are_unique()
    {
        var slugs = MarketplaceModuleCatalog.Manifests.Select(m => m.Slug).ToList();
        slugs.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Gate_only_modules_are_available_and_code_delivered_are_coming_soon()
    {
        foreach (var manifest in MarketplaceModuleCatalog.Manifests)
        {
            switch (manifest.Delivery)
            {
                case ModuleDelivery.GateOnly:
                    manifest.Status.Should().Be(ModuleStatus.Available,
                        $"gate-only module '{manifest.Slug}' works in the CE today");
                    break;
                case ModuleDelivery.CodeDelivered:
                    manifest.Status.Should().Be(ModuleStatus.ComingSoon,
                        $"code-delivered module '{manifest.Slug}' is not activatable until the module runtime (docs/33 §12)");
                    break;
            }
        }
    }

    [Fact]
    public void Every_manifest_has_display_and_lifecycle_metadata()
    {
        foreach (var m in MarketplaceModuleCatalog.Manifests)
        {
            m.Name.Should().NotBeNullOrWhiteSpace();
            m.Category.Should().NotBeNullOrWhiteSpace();
            m.Version.Should().MatchRegex(@"^\d+\.\d+\.\d+$", "the module version is a semver (== its changelog head)");
            m.Summary.Should().NotBeNullOrWhiteSpace();
            m.DocsAnchor.Should().Contain(m.Slug);
            m.ChangelogAnchor.Should().Contain(m.Slug);
            m.MinCeVersion.Should().MatchRegex(@"^\d+\.\d+\.\d+$");
        }
    }
}
