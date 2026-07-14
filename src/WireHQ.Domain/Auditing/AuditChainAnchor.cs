using WireHQ.Domain.Common;

namespace WireHQ.Domain.Auditing;

/// <summary>
/// A retention-boundary marker for a tenant's audit hash chain (ADR-031, docs/15 §5). When the retention
/// sweeper drops audit partitions, the oldest surviving row's predecessor is gone — so that row's
/// <c>prev_hash</c> would otherwise look like a broken link. An anchor records that boundary hash as a
/// legitimate cut, letting verification tell "history was retained away" apart from "rows were tampered
/// with" (a deletion with no matching anchor). Append-only, like the audit log itself; written only by the
/// owner-run sweeper, read by the verifier.
/// </summary>
public sealed class AuditChainAnchor : Entity
{
    // EF Core
    private AuditChainAnchor()
    {
    }

    private AuditChainAnchor(Guid id)
        : base(id)
    {
    }

    /// <summary>The tenant whose chain this anchor bounds. Null for the platform chain.</summary>
    public Guid? OrganizationId { get; private set; }

    /// <summary>The <c>prev_hash</c> the oldest surviving row legitimately carries after the retention cut.</summary>
    public byte[] BoundaryPrevHash { get; private set; } = null!;

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public static AuditChainAnchor Record(Guid? organizationId, byte[] boundaryPrevHash, DateTimeOffset createdAtUtc) =>
        new(Guid.CreateVersion7())
        {
            OrganizationId = organizationId,
            BoundaryPrevHash = boundaryPrevHash,
            CreatedAtUtc = createdAtUtc,
        };
}
