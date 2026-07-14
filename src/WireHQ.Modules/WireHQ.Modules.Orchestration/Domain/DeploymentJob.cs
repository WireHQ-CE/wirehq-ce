using WireHQ.Domain.Common;

namespace WireHQ.Modules.Orchestration.Domain;

public sealed record DeploymentJobQueued(Guid JobId, Guid OrganizationId, Guid InstanceId) : IDomainEvent;

/// <summary>
/// A unit of orchestration work: "make the target reflect the instance's desired config." Commands
/// enqueue it; the dispatcher claims and drives it through its lifecycle, recording a timeline of
/// <see cref="DeploymentEvent"/>s. Idempotent (carries an <see cref="IdempotencyKey"/> + the desired
/// config version), retried with backoff. Tenant-owned + audited. (docs/12-remote-orchestration.md §4)
/// </summary>
public sealed class DeploymentJob : AggregateRoot, ITenantOwned, IAuditable
{
    private readonly List<DeploymentEvent> _events = [];

    private DeploymentJob()
    {
    }

    private DeploymentJob(
        Guid id, Guid organizationId, Guid instanceId, DeploymentJobType type,
        int? desiredConfigVersion, string idempotencyKey, string? correlationId, DateTimeOffset nowUtc,
        string? reconvergeReason)
        : base(id)
    {
        OrganizationId = organizationId;
        InstanceId = instanceId;
        Type = type;
        DesiredConfigVersion = desiredConfigVersion;
        IdempotencyKey = idempotencyKey;
        CorrelationId = correlationId;
        Status = DeploymentJobStatus.Pending;

        // A drift-triggered auto-re-converge leads its timeline with the drift → re-converge lifecycle (docs/15 §8),
        // so the customer sees WHY the redeploy happened, before the normal queued → dispatched → applying flow.
        if (reconvergeReason is not null)
        {
            AddEvent("drift_detected", reconvergeReason, nowUtc);
            AddEvent("reconverge_requested", "Auto-re-converge enqueued a redeploy.", nowUtc);
        }

        AddEvent("queued", null, nowUtc);
    }

    public Guid OrganizationId { get; private set; }
    public Guid InstanceId { get; private set; }
    public DeploymentJobType Type { get; private set; }
    public DeploymentJobStatus Status { get; private set; }
    public int? DesiredConfigVersion { get; private set; }
    public string IdempotencyKey { get; private set; } = null!;

    /// <summary>The correlation id (W3C trace id) of the request that enqueued this job, so the job's
    /// background execution logs chain back to the originating request. Null for system-triggered jobs
    /// with no active trace. (ADR-030)</summary>
    public string? CorrelationId { get; private set; }
    public int Attempts { get; private set; }
    public string? Error { get; private set; }
    public DateTimeOffset? DispatchedAtUtc { get; private set; }
    public DateTimeOffset? CompletedAtUtc { get; private set; }

    public IReadOnlyCollection<DeploymentEvent> Events => _events.AsReadOnly();

    public DateTimeOffset CreatedAtUtc { get; private set; }
    public Guid? CreatedBy { get; private set; }
    public DateTimeOffset? UpdatedAtUtc { get; private set; }
    public Guid? UpdatedBy { get; private set; }

    public static DeploymentJob Queue(
        Guid organizationId, Guid instanceId, DeploymentJobType type,
        int? desiredConfigVersion, string idempotencyKey, string? correlationId, DateTimeOffset nowUtc,
        string? reconvergeReason = null)
    {
        var job = new DeploymentJob(
            Guid.CreateVersion7(), organizationId, instanceId, type, desiredConfigVersion, idempotencyKey, correlationId, nowUtc, reconvergeReason);
        job.Raise(new DeploymentJobQueued(job.Id, organizationId, instanceId));
        return job;
    }

    /// <summary>Claimed by the dispatcher. Pending → Dispatched (counts as an attempt).</summary>
    public void MarkDispatched(DateTimeOffset nowUtc)
    {
        Status = DeploymentJobStatus.Dispatched;
        DispatchedAtUtc = nowUtc;
        Attempts++;
        AddEvent("dispatched", null, nowUtc);
    }

    public void MarkApplying(DateTimeOffset nowUtc)
    {
        Status = DeploymentJobStatus.Applying;
        AddEvent("applying", null, nowUtc);
    }

    public void Succeed(DateTimeOffset nowUtc, string? note = null)
    {
        Status = DeploymentJobStatus.Succeeded;
        CompletedAtUtc = nowUtc;
        AddEvent("succeeded", note, nowUtc);
    }

    public void Fail(DateTimeOffset nowUtc, string error)
    {
        Status = DeploymentJobStatus.Failed;
        Error = error;
        CompletedAtUtc = nowUtc;
        AddEvent("failed", error, nowUtc);
    }

    private void AddEvent(string phase, string? detail, DateTimeOffset atUtc) =>
        _events.Add(DeploymentEvent.Create(Id, OrganizationId, phase, detail, atUtc));
}
