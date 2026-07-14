using WireHQ.Domain.Organizations;

namespace WireHQ.Application.Entitlements;

/// <summary>
/// The single owner of the entitlement <b>union</b>: an org's effective feature set = its edition's base plan
/// features ∪ the features unlocked by currently-valid activated Marketplace modules (docs/29 M-4). Two callers
/// share it, which is why the edition is a parameter rather than read from <c>ITenantContext</c>:
/// <list type="bullet">
/// <item><see cref="EntitlementService"/> resolves it for the <b>active</b> tenant (the pipeline gate, /auth/me,
/// the frontend hasFeature).</item>
/// <item>The <c>WireHQ.Api</c> API-key authentication handler resolves it for a <b>specific key-owning
/// org</b> in an anonymous, pre-tenant flow — where there is no active tenant yet, so it must pass the edition
/// it looked up under RLS bypass (docs/29 M-16). Without this, an <c>api-extensions</c>-activated CE org could
/// mint keys but not authenticate with them, because the handler resolved the edition-only base plan.</item>
/// </list>
/// Kept-core (M-17): the SaaS build binds <see cref="NoActivatedModules"/>, so the union is a strict no-op and
/// the main CI exercises the whole path. The activated-module set is install-global (not per-org, docs/29 M-5),
/// so the reader stays org-less; only the base-plan half varies by edition.
/// </summary>
public interface IEffectiveFeatureSet
{
    /// <summary>The effective feature set for <paramref name="edition"/> = base plan ∪ active-module features.</summary>
    Task<IReadOnlySet<string>> ResolveAsync(OrganizationEdition edition, CancellationToken cancellationToken);

    /// <summary>
    /// Whether the effective set for <paramref name="edition"/> includes <paramref name="feature"/>. Short-circuits
    /// on the base plan, so a SaaS caller never allocates a union just to test one key.
    /// </summary>
    Task<bool> HasFeatureAsync(OrganizationEdition edition, string feature, CancellationToken cancellationToken);
}

public sealed class EffectiveFeatureSet(IPlanCatalog catalog, IActivatedModuleReader modules) : IEffectiveFeatureSet
{
    public async Task<IReadOnlySet<string>> ResolveAsync(OrganizationEdition edition, CancellationToken cancellationToken)
    {
        var baseFeatures = catalog.For(edition).Features;
        var moduleFeatures = await modules.ActiveFeatureKeysAsync(cancellationToken);
        // Return the base set by reference when nothing is unlocked (the SaaS/no-modules path) so the caller can
        // skip re-wrapping the plan; only build a new set — never mutating the shared static plan — when a module
        // actually adds a feature (docs/29 M-4).
        return moduleFeatures.Count == 0
            ? baseFeatures
            : new HashSet<string>(baseFeatures.Concat(moduleFeatures));
    }

    public async Task<bool> HasFeatureAsync(OrganizationEdition edition, string feature, CancellationToken cancellationToken)
    {
        if (catalog.For(edition).Has(feature))
        {
            return true;
        }

        var moduleFeatures = await modules.ActiveFeatureKeysAsync(cancellationToken);
        return moduleFeatures.Contains(feature);
    }
}
