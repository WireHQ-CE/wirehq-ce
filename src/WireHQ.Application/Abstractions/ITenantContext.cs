namespace WireHQ.Application.Abstractions;

/// <summary>
/// The active tenant for the current request, resolved early in the pipeline from the
/// validated <c>org</c> claim (∩ the user's memberships). The persistence layer reads this to
/// apply the tenant query filter and to set the Postgres <c>app.current_org</c> GUC for RLS.
/// (docs/03-multi-tenancy.md)
/// </summary>
public interface ITenantContext
{
    Guid? OrganizationId { get; }

    string? OrganizationSlug { get; }

    /// <summary>True for platform-operator scope (separate from any tenant role).</summary>
    bool IsPlatformScope { get; }

    /// <summary>
    /// True when this unit of work legitimately operates across tenants (or before an org exists) and so
    /// must opt out of the Postgres RLS tenant policy — e.g. session minting, org provisioning, GetMe, the
    /// background dispatcher/reconciler's cross-tenant claim, and boot seeders. The persistence layer maps
    /// this (and <see cref="IsPlatformScope"/>) to the <c>app.bypass_rls</c> GUC. Default false ⇒ RLS is
    /// fail-closed: a request with no org and no bypass sees no tenant rows. (docs/03-multi-tenancy.md, ADR-027)
    /// </summary>
    bool BypassTenantIsolation { get; }

    bool HasTenant => OrganizationId is not null;
}
