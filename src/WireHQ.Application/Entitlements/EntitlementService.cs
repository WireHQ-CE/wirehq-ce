using Microsoft.EntityFrameworkCore;
using WireHQ.Application.Abstractions;
using WireHQ.Application.Abstractions.Persistence;
using WireHQ.Domain.Organizations;
using WireHQ.Shared.Results;

namespace WireHQ.Application.Entitlements;

/// <summary>The active org's plan + what it includes, for UI gating (returned via <c>/me</c> + the plan page).</summary>
public sealed record EntitlementSnapshot(
    string Plan,
    IReadOnlyCollection<string> Features,
    IReadOnlyDictionary<string, int> Limits);

/// <summary>
/// Resolves the active organisation's plan (its <see cref="OrganizationEdition"/>) to feature + quota
/// entitlements, and enforces quotas. The plan is read fresh from the org and memoised for the request
/// scope. (docs/commercial.md §6)
/// </summary>
public interface IEntitlementService
{
    /// <summary>True if the active org's plan includes the feature (no org ⇒ falls back to Community).</summary>
    Task<bool> HasFeatureAsync(string feature, CancellationToken cancellationToken);

    /// <summary>
    /// Fails with <c>plan.limit_reached</c> when adding one more of <paramref name="resource"/> would exceed
    /// the plan's quota; succeeds when under the cap or the plan is unlimited.
    /// </summary>
    Task<Result> EnsureCanAddAsync(PlanResource resource, int currentCount, CancellationToken cancellationToken);

    /// <summary>The plan's features + limits for UI gating.</summary>
    Task<EntitlementSnapshot> SnapshotAsync(CancellationToken cancellationToken);

    /// <summary>
    /// The active org's customer-visible audit retention window (<c>null</c> = unlimited). The audit read
    /// clamps results to this window, so each edition sees back as far as its plan allows. (docs/15 §5)
    /// </summary>
    Task<TimeSpan?> AuditRetentionWindowAsync(CancellationToken cancellationToken);
}

public sealed class EntitlementService(
    IApplicationDbContext dbContext,
    ITenantContext tenant,
    IPlanCatalog catalog,
    IEffectiveFeatureSet effectiveFeatures)
    : IEntitlementService
{
    private (OrganizationEdition Edition, PlanDefinition Plan)? _cached;

    public async Task<bool> HasFeatureAsync(string feature, CancellationToken cancellationToken)
    {
        var (_, plan) = await ResolveAsync(cancellationToken);
        return plan.Has(feature);
    }

    public async Task<Result> EnsureCanAddAsync(PlanResource resource, int currentCount, CancellationToken cancellationToken)
    {
        var (edition, plan) = await ResolveAsync(cancellationToken);
        if (plan.IsUnlimited(resource))
        {
            return Result.Success();
        }

        var limit = plan.Limit(resource);
        return currentCount < limit
            ? Result.Success()
            : Error.Conflict(
                "plan.limit_reached",
                $"Your {edition} plan allows up to {limit} {resource.ToString().ToLowerInvariant()}. Upgrade your plan to add more.");
    }

    public async Task<EntitlementSnapshot> SnapshotAsync(CancellationToken cancellationToken)
    {
        var (edition, plan) = await ResolveAsync(cancellationToken);
        var limits = Enum.GetValues<PlanResource>().ToDictionary(r => char.ToLowerInvariant(r.ToString()[0]) + r.ToString()[1..], plan.Limit);
        return new EntitlementSnapshot(edition.ToString(), plan.Features.ToArray(), limits);
    }

    public async Task<TimeSpan?> AuditRetentionWindowAsync(CancellationToken cancellationToken)
    {
        var (edition, _) = await ResolveAsync(cancellationToken);
        return catalog.AuditRetentionWindow(edition);
    }

    private async Task<(OrganizationEdition Edition, PlanDefinition Plan)> ResolveAsync(CancellationToken cancellationToken)
    {
        if (_cached is { } cached)
        {
            return cached;
        }

        var edition = OrganizationEdition.Community;
        if (tenant.OrganizationId is { } organizationId)
        {
            edition = await dbContext.Organizations
                .Where(o => o.Id == organizationId)
                .Select(o => o.Edition)
                .FirstOrDefaultAsync(cancellationToken);
        }

        var basePlan = catalog.For(edition);

        // The EFFECTIVE plan = the edition's base features UNION the features unlocked by valid, activated
        // Marketplace modules (docs/29 M-4), computed by the shared IEffectiveFeatureSet — the one owner of the
        // union, so the anonymous API-key auth path resolves the same set (M-16). No-op in SaaS (the reader
        // returns empty → the base feature set is returned by reference, so the base plan is reused untouched);
        // on a CE install this is how a purchased-and-activated module lights up its capability across the
        // pipeline gate, /auth/me, and the frontend at once. The quota limits always come from the base plan
        // (v1 modules are feature-only). A new feature set is built only when a module adds one — the shared
        // static plan is never mutated.
        var effective = await effectiveFeatures.ResolveAsync(edition, cancellationToken);
        var plan = ReferenceEquals(effective, basePlan.Features)
            ? basePlan
            : basePlan with { Features = effective };

        var resolved = (edition, plan);
        _cached = resolved;
        return resolved;
    }
}
