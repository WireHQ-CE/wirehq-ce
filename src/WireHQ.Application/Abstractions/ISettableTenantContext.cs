namespace WireHQ.Application.Abstractions;

/// <summary>
/// Lets trusted background/system processes establish the active tenant for a unit of work when there
/// is no HTTP request to resolve it from — e.g. the deployment-job dispatcher, the reconciler, and the
/// agent gateway, which process work across tenants. The implementation is the same scoped object as
/// <see cref="ITenantContext"/>, so once set, the persistence layer's tenant query filter and the audit
/// writer scope correctly for the rest of that scope. (docs/12-remote-orchestration.md §4)
/// </summary>
public interface ISettableTenantContext
{
    void SetTenant(Guid organizationId, string? slug = null);

    /// <summary>
    /// Opt this unit of work out of the RLS tenant policy for legitimate cross-tenant / pre-org work
    /// (session minting, org provisioning, GetMe, the dispatcher/reconciler claim, boot seeders). Setting
    /// a concrete tenant via <see cref="SetTenant"/> clears it again. (ADR-027)
    /// </summary>
    void SetBypass();
}
