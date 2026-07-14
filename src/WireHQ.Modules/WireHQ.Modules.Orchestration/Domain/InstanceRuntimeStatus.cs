using WireHQ.Domain.Common;

namespace WireHQ.Modules.Orchestration.Domain;

/// <summary>
/// The last <b>observed</b> runtime state of an instance on its target — distinct from the desired
/// config (<c>config_versions</c>) and the deployment binding (<c>deployment_targets</c>). One row per
/// instance, upserted by the status reconciler / on-demand refresh. Holds the observed provider state and
/// the config-drift verdict (desired vs deployed config hash). Tenant-owned, audited.
/// (docs/12-remote-orchestration.md §7/§10)
/// </summary>
public sealed class InstanceRuntimeStatus : AggregateRoot, ITenantOwned, IAuditable
{
    private InstanceRuntimeStatus()
    {
    }

    private InstanceRuntimeStatus(Guid id, Guid organizationId, Guid instanceId)
        : base(id)
    {
        OrganizationId = organizationId;
        InstanceId = instanceId;
    }

    public Guid OrganizationId { get; private set; }
    public Guid InstanceId { get; private set; }

    /// <summary>The observed provider state name (e.g. <c>Running</c>/<c>Stopped</c>), or null if never observed.</summary>
    public string? State { get; private set; }

    /// <summary>sha256 of WireHQ's desired server config at the last observation.</summary>
    public string? DesiredConfigHash { get; private set; }

    /// <summary>sha256 of the config actually deployed on the host at the last observation.</summary>
    public string? ActualConfigHash { get; private set; }

    public bool HasDrift { get; private set; }
    public string? DriftDetail { get; private set; }
    public DateTimeOffset? ObservedAtUtc { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }
    public Guid? CreatedBy { get; private set; }
    public DateTimeOffset? UpdatedAtUtc { get; private set; }
    public Guid? UpdatedBy { get; private set; }

    public static InstanceRuntimeStatus Create(Guid organizationId, Guid instanceId) =>
        new(Guid.CreateVersion7(), organizationId, instanceId);

    /// <summary>Records a fresh observation (state + drift verdict).</summary>
    public void Record(string? state, string? desiredHash, string? actualHash, bool hasDrift, string? driftDetail, DateTimeOffset observedAtUtc)
    {
        State = state;
        DesiredConfigHash = desiredHash;
        ActualConfigHash = actualHash;
        HasDrift = hasDrift;
        DriftDetail = driftDetail;
        ObservedAtUtc = observedAtUtc;
    }
}
