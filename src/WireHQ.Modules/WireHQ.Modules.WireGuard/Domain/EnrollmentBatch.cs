using WireHQ.Domain.Common;

namespace WireHQ.Modules.WireGuard.Domain;

/// <summary>
/// A CSV bulk-enrollment job: tracks validation/preview/import progress and the per-row outcome
/// summary. Tenant-owned + audited. (docs/11-wireguard-module.md §7)
/// </summary>
public sealed class EnrollmentBatch : Entity, ITenantOwned, IAuditable
{
    private EnrollmentBatch()
    {
    }

    private EnrollmentBatch(Guid id, Guid organizationId, Guid instanceId, string sourceFilename)
        : base(id)
    {
        OrganizationId = organizationId;
        InstanceId = instanceId;
        SourceFilename = sourceFilename;
        Status = EnrollmentBatchStatus.Validating;
    }

    public Guid OrganizationId { get; private set; }
    public Guid InstanceId { get; private set; }
    public string SourceFilename { get; private set; } = null!;
    public EnrollmentBatchStatus Status { get; private set; }
    public int TotalRows { get; private set; }
    public int ValidRows { get; private set; }
    public int ErrorRows { get; private set; }

    /// <summary>Per-row outcomes (jsonb).</summary>
    public string? Summary { get; private set; }

    public DateTimeOffset? CompletedAtUtc { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }
    public Guid? CreatedBy { get; private set; }
    public DateTimeOffset? UpdatedAtUtc { get; private set; }
    public Guid? UpdatedBy { get; private set; }

    public static EnrollmentBatch Start(Guid organizationId, Guid instanceId, string sourceFilename) =>
        new(Guid.CreateVersion7(), organizationId, instanceId, sourceFilename);

    public void MarkPreviewed(int total, int valid, int errors, string summaryJson)
    {
        TotalRows = total;
        ValidRows = valid;
        ErrorRows = errors;
        Summary = summaryJson;
        Status = EnrollmentBatchStatus.Previewed;
    }

    public void MarkImporting() => Status = EnrollmentBatchStatus.Importing;

    public void MarkCompleted(DateTimeOffset nowUtc)
    {
        Status = EnrollmentBatchStatus.Completed;
        CompletedAtUtc = nowUtc;
    }

    /// <summary>Records the final counts + per-row outcome summary and marks the batch completed (one transaction).</summary>
    public void Complete(int total, int created, int errored, string summaryJson, DateTimeOffset nowUtc)
    {
        TotalRows = total;
        ValidRows = created;
        ErrorRows = errored;
        Summary = summaryJson;
        Status = EnrollmentBatchStatus.Completed;
        CompletedAtUtc = nowUtc;
    }

    public void MarkFailed(DateTimeOffset nowUtc, string summaryJson)
    {
        Status = EnrollmentBatchStatus.Failed;
        Summary = summaryJson;
        CompletedAtUtc = nowUtc;
    }
}
