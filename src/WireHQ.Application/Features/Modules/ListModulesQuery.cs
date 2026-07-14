using Microsoft.EntityFrameworkCore;
using WireHQ.Application.Abstractions.Persistence;
using WireHQ.Application.Authorization;
using WireHQ.Application.Common.Messaging;
using WireHQ.Application.Entitlements;
using WireHQ.Shared.Results;

namespace WireHQ.Application.Features.Modules;

// The install's activated module licences, for the Modules console (docs/29 M-9). Returns the raw activation
// state per slug; the console overlays it on the display catalogue (web/src/lib/marketplace/catalog.ts) and
// derives the badge (Active / Lapsed / Revoked). CE-only (overlay-added).

public sealed record ListModulesQuery : IQuery<IReadOnlyList<ActivatedModuleView>>, IAuthorizedRequest
{
    public IReadOnlyCollection<string> RequiredPermissions => [Permissions.Modules.Manage];
}

/// <summary>One activated module's state on this install. <see cref="Status"/> is the stored lifecycle
/// (<c>Active</c>/<c>Revoked</c>); a non-null <see cref="GraceEndsUtc"/> in the past means lapsed (Wave 3).
/// <see cref="Features"/> are the plan feature keys the module unlocks (from the catalogue), so the console can
/// confirm the capability lit up (<c>hasFeature</c>).</summary>
public sealed record ActivatedModuleView(
    string Slug, string Status, DateTimeOffset ActivatedAtUtc, DateTimeOffset? GraceEndsUtc, IReadOnlyList<string> Features);

public sealed class ListModulesQueryHandler(IApplicationDbContext dbContext)
    : IQueryHandler<ListModulesQuery, IReadOnlyList<ActivatedModuleView>>
{
    public async Task<Result<IReadOnlyList<ActivatedModuleView>>> Handle(ListModulesQuery query, CancellationToken cancellationToken)
    {
        var licences = await dbContext.ModuleLicences
            .AsNoTracking()
            .OrderBy(l => l.ModuleSlug)
            .ToListAsync(cancellationToken);

        IReadOnlyList<ActivatedModuleView> views = licences
            .Select(l => new ActivatedModuleView(
                l.ModuleSlug,
                l.Status.ToString(),
                l.ActivatedAtUtc,
                l.GraceEndsUtc,
                ModuleCatalog.Find(l.ModuleSlug)?.Features.ToArray() ?? []))
            .ToList();

        return Result.Success(views);
    }
}
