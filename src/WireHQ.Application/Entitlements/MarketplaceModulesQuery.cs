using WireHQ.Application.Common.Messaging;
using WireHQ.Shared.Results;

namespace WireHQ.Application.Entitlements;

// The public Marketplace module catalogue (docs/33 §5.2, ADR-048). Anonymous + org-less — read by both the SaaS
// marketplace marketing pages and the CE Modules console, which overlays each install's activation state on top.
// Sourced from the in-code MarketplaceModuleCatalog (kept-core), so it is a pure, DB-free read. Enum fields are
// projected to strings to avoid any enum-serialised-as-int pitfall on the wire (docs/30 lesson).

/// <summary>One Marketplace module's public manifest, as sent to the browser.</summary>
public sealed record MarketplaceModuleView(
    string Slug,
    string Name,
    string Category,
    string Version,
    string Summary,
    string DocsAnchor,
    string ChangelogAnchor,
    string MinCeVersion,
    string Tier,
    string Status,
    string? Delivery);

/// <summary>List the manifest for every backed Marketplace module, for public display. Anonymous + org-less.</summary>
public sealed record PublicMarketplaceModulesQuery : IQuery<IReadOnlyList<MarketplaceModuleView>>, ITenantUnscopedRequest;

public sealed class PublicMarketplaceModulesQueryHandler
    : IQueryHandler<PublicMarketplaceModulesQuery, IReadOnlyList<MarketplaceModuleView>>
{
    public Task<Result<IReadOnlyList<MarketplaceModuleView>>> Handle(
        PublicMarketplaceModulesQuery query, CancellationToken cancellationToken)
    {
        IReadOnlyList<MarketplaceModuleView> views = MarketplaceModuleCatalog.Manifests
            .Select(m => new MarketplaceModuleView(
                m.Slug, m.Name, m.Category, m.Version, m.Summary, m.DocsAnchor, m.ChangelogAnchor,
                m.MinCeVersion, m.Tier.ToString(), m.Status.ToString(), m.Delivery?.ToString()))
            .ToList();

        return Task.FromResult(Result.Success(views));
    }
}
