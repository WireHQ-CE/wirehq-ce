using Microsoft.EntityFrameworkCore;
using WireHQ.Application.Abstractions.Persistence;
using WireHQ.Domain.Organizations;

namespace WireHQ.Application.Entitlements;

/// <summary>
/// Re-checks the live entitlement <b>union</b> for organisations from a BACKGROUND sweep that runs under RLS bypass
/// with no active tenant (the webhook / notification / directory drains). This is the MM-14 data-plane deactivation
/// guard (docs/33 §5.4): a job or delivery captured while a module was active must NOT keep running once the customer
/// deactivates the module (self-host) or downgrades the plan (SaaS) — so every gated item is re-checked against the
/// live union at <b>drain</b> time, not only at capture time. It batch-loads each org's edition once and caches the
/// resolved feature set per <b>distinct edition</b> (the activated-module set is install-global — docs/29 M-5 — so the
/// union depends only on edition), so the guard costs at most one query per distinct edition per sweep.
/// <para>
/// Fail-closed: an org whose edition cannot be resolved is treated as NOT entitled. Create one per sweep inside the
/// bypass scope — it holds per-sweep caches, so it is NOT a DI singleton. Kept-core: it ships in every edition; in the
/// SaaS build the union collapses to the base plan (docs/29 M-17), so it just re-tests the plan.
/// </para>
/// </summary>
public sealed class BackgroundEntitlementResolver(IApplicationDbContext dbContext, IEffectiveFeatureSet effectiveFeatures)
{
    // Edition per org — a null value is a memoised "queried, not found" so a missing org never re-queries.
    private readonly Dictionary<Guid, OrganizationEdition?> _editionByOrg = new();
    private readonly Dictionary<OrganizationEdition, IReadOnlySet<string>> _featuresByEdition = new();

    /// <summary>
    /// Pre-load the editions for these organisations in a single query (call once with the sweep's gated org ids).
    /// Every requested id is recorded — including ones with no matching row (as a not-found sentinel) — so neither a
    /// repeat call nor the lazy path in <see cref="IsEntitledAsync"/> re-queries it.
    /// </summary>
    public async Task LoadEditionsAsync(IEnumerable<Guid> organizationIds, CancellationToken cancellationToken)
    {
        var missing = organizationIds.Where(id => !_editionByOrg.ContainsKey(id)).Distinct().ToList();
        if (missing.Count == 0)
        {
            return;
        }

        var found = await dbContext.Organizations
            .IgnoreQueryFilters()
            .Where(o => missing.Contains(o.Id))
            .Select(o => new { o.Id, o.Edition })
            .ToDictionaryAsync(o => o.Id, o => o.Edition, cancellationToken);
        foreach (var id in missing)
        {
            _editionByOrg[id] = found.TryGetValue(id, out var edition) ? edition : null;
        }
    }

    /// <summary>
    /// Whether <paramref name="organizationId"/>'s edition (+ install-global activated modules) currently includes
    /// <paramref name="feature"/>. Lazily loads the edition if it was not pre-loaded. Fail-closed on an unknown org.
    /// </summary>
    public async Task<bool> IsEntitledAsync(Guid organizationId, string feature, CancellationToken cancellationToken)
    {
        if (!_editionByOrg.TryGetValue(organizationId, out var edition))
        {
            await LoadEditionsAsync([organizationId], cancellationToken);
            _editionByOrg.TryGetValue(organizationId, out edition);
        }

        if (edition is null)
        {
            return false;
        }

        if (!_featuresByEdition.TryGetValue(edition.Value, out var set))
        {
            set = await effectiveFeatures.ResolveAsync(edition.Value, cancellationToken);
            _featuresByEdition[edition.Value] = set;
        }

        return set.Contains(feature);
    }
}
