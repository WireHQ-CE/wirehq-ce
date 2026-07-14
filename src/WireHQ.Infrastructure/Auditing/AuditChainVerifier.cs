using Microsoft.EntityFrameworkCore;
using WireHQ.Application.Abstractions;
using WireHQ.Application.Abstractions.Persistence;
using WireHQ.Domain.Auditing;

namespace WireHQ.Infrastructure.Auditing;

/// <summary>
/// Re-derives a tenant's audit hash chain from its stored rows and checks each link, walking the same
/// (occurred-at, id) order the chain is built in. Reads only — audit rows are append-only and the
/// table carries no RLS, so scoping by <c>organization_id</c> is sufficient. (ADR-031, docs/15 §5)
/// </summary>
public sealed class AuditChainVerifier(IApplicationDbContext dbContext) : IAuditChainVerifier
{
    public async Task<AuditChainVerificationResult> VerifyAsync(Guid? organizationId, CancellationToken cancellationToken)
    {
        var rows = await dbContext.AuditLogs
            .Where(a => a.OrganizationId == organizationId)
            .OrderBy(a => a.OccurredAtUtc)
            .ThenBy(a => a.Id)
            .ToListAsync(cancellationToken);

        // Retention may have dropped this tenant's oldest rows; the recorded anchors say which boundary
        // prev_hashes are legitimate cuts, so a retained-away history doesn't read as tampering. (slice 4b)
        var anchors = await dbContext.AuditChainAnchors
            .Where(a => a.OrganizationId == organizationId)
            .Select(a => a.BoundaryPrevHash)
            .ToListAsync(cancellationToken);

        return Verify(rows, anchors.Select(Convert.ToHexString).ToHashSet());
    }

    /// <summary>
    /// Verifies an already-ordered run of one chain's entries. Shared by the read-side verifier and tests.
    /// The first surviving entry must either be the genesis (null <c>prev_hash</c>) or sit on a recorded
    /// retention boundary (its <c>prev_hash</c> is in <paramref name="validBoundaryHashes"/>); every link
    /// thereafter must reproduce its stored hash. A non-null first <c>prev_hash</c> with no matching anchor
    /// means rows were removed without a retention record — i.e. tampering.
    /// </summary>
    public static AuditChainVerificationResult Verify(
        IReadOnlyList<AuditLog> orderedEntries, ISet<string>? validBoundaryHashes = null)
    {
        byte[]? prev = null;
        var verified = 0;
        var isFirst = true;

        foreach (var entry in orderedEntries)
        {
            if (entry.EntryHash is null)
            {
                return AuditChainVerificationResult.Broken(
                    verified, entry.Id, "Entry has no chain hash (unchained or tampered row).");
            }

            if (isFirst)
            {
                if (entry.PrevHash is not null
                    && (validBoundaryHashes is null || !validBoundaryHashes.Contains(Convert.ToHexString(entry.PrevHash))))
                {
                    return AuditChainVerificationResult.Broken(
                        verified, entry.Id,
                        "Chain starts mid-chain with no matching retention anchor — rows were removed or the chain was tampered with.");
                }

                prev = entry.PrevHash; // genesis (null) or an anchored retention boundary
                isFirst = false;
            }
            else if (!ByteEquals(entry.PrevHash, prev))
            {
                return AuditChainVerificationResult.Broken(
                    verified, entry.Id, "Entry's prev_hash does not match the preceding entry — a row was inserted, removed, or reordered.");
            }

            var expected = AuditChain.ComputeEntryHash(prev, entry);
            if (!ByteEquals(entry.EntryHash, expected))
            {
                return AuditChainVerificationResult.Broken(
                    verified, entry.Id, "Entry's content does not match its hash — the row was altered after it was written.");
            }

            prev = entry.EntryHash;
            verified++;
        }

        return AuditChainVerificationResult.Intact(verified);
    }

    private static bool ByteEquals(byte[]? a, byte[]? b)
    {
        if (a is null || b is null)
        {
            return a is null && b is null;
        }

        return a.AsSpan().SequenceEqual(b);
    }
}
