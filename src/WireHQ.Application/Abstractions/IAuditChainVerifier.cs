namespace WireHQ.Application.Abstractions;

/// <summary>
/// Re-computes a tenant's audit hash chain from its stored rows and reports whether it is intact
/// (ADR-031, docs/15 §5). A break means a row was altered or removed out from under the chain — the
/// tamper-evidence signal. Implemented in Infrastructure where the canonical hashing lives.
/// </summary>
public interface IAuditChainVerifier
{
    Task<AuditChainVerificationResult> VerifyAsync(Guid? organizationId, CancellationToken cancellationToken);
}

/// <summary>
/// The outcome of verifying a chain. <see cref="IsIntact"/> is the headline; on a break,
/// <see cref="BrokenAtEntryId"/> + <see cref="Detail"/> locate it.
/// </summary>
public sealed record AuditChainVerificationResult(
    bool IsIntact,
    int VerifiedCount,
    Guid? BrokenAtEntryId,
    string? Detail)
{
    public static AuditChainVerificationResult Intact(int verifiedCount) => new(true, verifiedCount, null, null);

    public static AuditChainVerificationResult Broken(int verifiedCount, Guid brokenAtEntryId, string detail) =>
        new(false, verifiedCount, brokenAtEntryId, detail);
}
