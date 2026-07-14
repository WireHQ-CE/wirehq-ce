using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using WireHQ.Domain.Organizations;

namespace WireHQ.Infrastructure.Persistence.Seeding;

/// <summary>
/// A one-shot, idempotent re-edition of existing organisations from the OLD Community Edition default
/// (<see cref="OrganizationEdition.Enterprise"/>, which provisioned every CE org uncapped) to the lean
/// <see cref="OrganizationEdition.CommunityEdition"/> free core (docs/29-ce-marketplace-modules.md M-15). Flipping
/// the default edition only affects NEWLY-provisioned orgs, so without this an upgrading CE install would keep
/// every existing org uncapped-Enterprise and the Marketplace would still have nothing to sell to it.
///
/// <para>This is a <b>feature-removing</b> change for an existing install — the org's previously-free premium
/// capabilities now need an activated module licence — so it is <b>opt-in</b> and announced in the CE release
/// notes: it runs only when <c>Entitlements:ReeditionExistingOrgs=true</c> (default off), and the nag-don't-kill
/// grace on any already-activated module softens the transition.</para>
///
/// <para>Triple-guarded so it can never touch a SaaS org (M-15): (1) this file is CE-ONLY (overlay-added, absent
/// from the SaaS build); (2) it is registered ONLY by the CE <c>AddActivatedModules</c> seam, never by the shared
/// Infrastructure DI; (3) the config flag defaults off. Runs under the boot bypass (Program.cs sets it before
/// seeding), so it sees + updates all orgs cross-tenant.</para>
/// </summary>
public sealed class CommunityEditionReeditionSeeder(
    ApplicationDbContext dbContext,
    IConfiguration configuration,
    ILogger<CommunityEditionReeditionSeeder> logger) : IDataSeeder
{
    public int Order => 70;

    public async Task SeedAsync(CancellationToken cancellationToken = default)
    {
        if (!string.Equals(configuration["Entitlements:ReeditionExistingOrgs"]?.Trim(), "true", StringComparison.OrdinalIgnoreCase))
        {
            return; // Opt-in: an existing install stays as-is until the operator deliberately re-editions it.
        }

        var orgs = await dbContext.Organizations
            .IgnoreQueryFilters()
            .Where(o => o.Edition == OrganizationEdition.Enterprise)
            .ToListAsync(cancellationToken);
        if (orgs.Count == 0)
        {
            return; // Idempotent — nothing left on the old default (a fresh CE provisions CommunityEdition directly).
        }

        foreach (var organization in orgs)
        {
            organization.SetEdition(OrganizationEdition.CommunityEdition);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        // A feature-removing action — log which orgs (usually one on a self-hosted install) so the operator has a
        // record of what changed; SetEdition is a plain state change, so this log is the audit trail.
        logger.LogWarning(
            "CE Marketplace: re-editioned {Count} organisation(s) from Enterprise to CommunityEdition (lean free core; " +
            "premium capabilities now require an activated module licence — docs/29 M-15). Organisation ids: {OrgIds}.",
            orgs.Count, orgs.Select(o => o.Id));
    }
}
