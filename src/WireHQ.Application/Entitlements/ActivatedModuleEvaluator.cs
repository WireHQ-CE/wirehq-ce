using WireHQ.Domain.Modules;

namespace WireHQ.Application.Entitlements;

/// <summary>
/// Pure, clock-injected evaluation of which plan feature keys are currently unlocked by a set of stored,
/// activated Marketplace module licences on a CE install. This is the bug-prone half of the CE unlock — the
/// revoke / offline-grace / gate-only rules — so it is kept-core and unit-tested on the main CI; the CE-only
/// persistence adapter (<c>ActivatedModuleReader</c>) does nothing but map <c>module_licences</c> rows onto
/// <see cref="ActivatedModuleRecord"/> and call this. (docs/29-ce-marketplace-modules.md M-4/M-7/M-8/M-17)
/// </summary>
public static class ActivatedModuleEvaluator
{
    /// <summary>
    /// The feature keys granted by the currently-valid licences in <paramref name="licences"/> at
    /// <paramref name="nowUtc"/>. A licence grants nothing if it is <see cref="ModuleLicenceStatus.Revoked"/>
    /// (authoritative disable, M-7), past its <see cref="ActivatedModuleRecord.GraceEndsUtc"/> grace window
    /// (lapsed — nag-don't-kill: the capability locks but the control plane never faults, M-7), names an
    /// unknown module slug, or names a <see cref="ModuleDelivery.CodeDelivered"/> module (whose capability code
    /// is stripped from the CE, so unlocking its entitlement would light a dead feature — defence-in-depth
    /// behind the activation endpoint's own refusal, M-8).
    /// </summary>
    public static IReadOnlySet<string> ActiveFeatureKeys(IEnumerable<ActivatedModuleRecord> licences, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(licences);

        var features = new HashSet<string>();
        foreach (var licence in licences)
        {
            if (licence.Status == ModuleLicenceStatus.Revoked)
            {
                continue;
            }

            // A null grace boundary means "activated, not yet online-verified" (Wave 2) — still valid. A set
            // boundary that is in the past means the offline grace has lapsed → the capability locks until a
            // re-verify (M-7). `<=` so the boundary instant itself is already expired.
            if (licence.GraceEndsUtc is { } graceEnds && graceEnds <= nowUtc)
            {
                continue;
            }

            var module = ModuleCatalog.Find(licence.ModuleSlug);
            if (module is null || module.Delivery != ModuleDelivery.GateOnly)
            {
                continue;
            }

            features.UnionWith(module.Features);
        }

        return features;
    }
}
