using Microsoft.EntityFrameworkCore;
using WireHQ.Application.Abstractions;
using WireHQ.Application.Abstractions.Persistence;
using WireHQ.Application.Entitlements;

namespace WireHQ.Application.Features.Modules;

/// <summary>
/// The Community Edition implementation of <see cref="IActivatedModuleReader"/>
/// (docs/29-ce-marketplace-modules.md M-4/M-17): reads the install's stored module licences and delegates the
/// revoke / offline-grace / gate-only rules to the kept-core <see cref="ActivatedModuleEvaluator"/>. Bound by
/// the CE overlay's <c>AddActivatedModules</c> seam, replacing the no-op <see cref="NoActivatedModules"/>.
///
/// <para>The <c>modules</c> tables are install-global — no <c>organization_id</c> and no RLS policy — so this
/// returns the same feature set for every org on the install and is safe to resolve on the anonymous API-key
/// auth path (which runs under RLS bypass, docs/29 M-5/M-16). CE-only (overlay-added).</para>
/// </summary>
public sealed class ActivatedModuleReader(IApplicationDbContext dbContext, IDateTimeProvider clock) : IActivatedModuleReader
{
    public async Task<IReadOnlySet<string>> ActiveFeatureKeysAsync(CancellationToken cancellationToken)
    {
        // The table holds at most a handful of rows (one per activated module), so materialise then map in
        // memory — this reads the value-converted Status cleanly and keeps the evaluator a pure function.
        var licences = await dbContext.ModuleLicences.AsNoTracking().ToListAsync(cancellationToken);

        return ActivatedModuleEvaluator.ActiveFeatureKeys(
            licences.Select(l => new ActivatedModuleRecord(l.ModuleSlug, l.Status, l.GraceEndsUtc)),
            clock.UtcNow);
    }
}
