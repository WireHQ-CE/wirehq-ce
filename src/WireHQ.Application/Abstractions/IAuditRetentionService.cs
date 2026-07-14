namespace WireHQ.Application.Abstractions;

/// <summary>
/// Physical retention for the partitioned audit log (ADR-031, docs/15 §5). One pass keeps upcoming month
/// partitions carved out, drops whole partitions older than the configured ceiling, and records the
/// hash-chain re-anchors that keep verification meaningful after a drop. Runs as the owner (the app role is
/// append-only and cannot drop partitions). Per-edition retention *windows* are a read-side visibility
/// concern; this is the platform-wide physical floor.
/// </summary>
public interface IAuditRetentionService
{
    /// <summary>One pass. Returns the number of partitions dropped.</summary>
    Task<int> SweepAsync(CancellationToken cancellationToken);
}
