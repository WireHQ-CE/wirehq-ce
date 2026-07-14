using WireHQ.Domain.Common;

namespace WireHQ.Modules.Orchestration.Domain;

/// <summary>
/// An immutable entry in a <see cref="DeploymentJob"/>'s timeline (queued/dispatched/applying/
/// succeeded/failed). Always loaded via the job (a child of the aggregate), so it carries the org id
/// for convenience but is isolated through the job's tenant filter rather than its own.
/// </summary>
public sealed class DeploymentEvent : Entity
{
    private DeploymentEvent()
    {
    }

    private DeploymentEvent(Guid id, Guid deploymentJobId, Guid organizationId, string phase, string? detail, DateTimeOffset atUtc)
        : base(id)
    {
        DeploymentJobId = deploymentJobId;
        OrganizationId = organizationId;
        Phase = phase;
        Detail = detail;
        AtUtc = atUtc;
    }

    public Guid DeploymentJobId { get; private set; }
    public Guid OrganizationId { get; private set; }
    public string Phase { get; private set; } = null!;
    public string? Detail { get; private set; }
    public DateTimeOffset AtUtc { get; private set; }

    public static DeploymentEvent Create(Guid deploymentJobId, Guid organizationId, string phase, string? detail, DateTimeOffset atUtc) =>
        new(Guid.CreateVersion7(), deploymentJobId, organizationId, phase, detail, atUtc);
}
