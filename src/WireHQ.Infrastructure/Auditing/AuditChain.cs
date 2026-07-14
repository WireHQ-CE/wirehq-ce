using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using WireHQ.Domain.Auditing;

namespace WireHQ.Infrastructure.Auditing;

/// <summary>
/// The tamper-evidence primitives for the per-tenant audit hash chain (ADR-031, docs/15 §5).
///
/// One source of truth for (a) the canonical byte representation of an audit entry, (b) the entry
/// hash that links it to the previous entry, and (c) the per-tenant advisory-lock key that serialises
/// concurrent writers to a chain. The runtime interceptor, the boot backfill, and the verifier all go
/// through here, so a record written today verifies byte-for-byte tomorrow.
/// </summary>
public static class AuditChain
{
    private const char FieldSeparator = '\u001F'; // ASCII Unit Separator — cannot appear in our field values.

    /// <summary>Namespacing salt so an audit-chain advisory key never collides with another subsystem's.</summary>
    private static readonly byte[] LockNamespace = "wirehq.audit-chain"u8.ToArray();

    /// <summary>
    /// The entry hash = SHA-256(prevHash ‖ canonical(entry)). The genesis entry (no predecessor) passes a
    /// null/empty prevHash, so the chain is still anchored to the entry's own canonical content.
    /// </summary>
    public static byte[] ComputeEntryHash(byte[]? prevHash, AuditLog entry)
    {
        var canonical = CanonicalBytes(entry);
        using var sha = SHA256.Create();
        var seed = prevHash ?? [];
        var buffer = new byte[seed.Length + canonical.Length];
        Buffer.BlockCopy(seed, 0, buffer, 0, seed.Length);
        Buffer.BlockCopy(canonical, 0, buffer, seed.Length, canonical.Length);
        return sha.ComputeHash(buffer);
    }

    /// <summary>
    /// A deterministic, version-stable serialisation of the entry's immutable content. Fixed field order,
    /// invariant formatting, position-delimited — so nulls are distinguishable and the digest is reproducible
    /// across processes and time. The chain columns themselves are intentionally excluded.
    /// </summary>
    public static byte[] CanonicalBytes(AuditLog entry)
    {
        var builder = new StringBuilder();
        Append(builder, entry.Id.ToString("D", CultureInfo.InvariantCulture));
        // Microsecond precision so the digest survives the timestamptz round-trip: Postgres stores
        // microseconds (truncating .NET's 100 ns ticks) and the "ffffff" specifier truncates identically,
        // so the value hashed at write time equals the value re-read at verification time.
        Append(builder, entry.OccurredAtUtc.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture));
        Append(builder, entry.OrganizationId?.ToString("D", CultureInfo.InvariantCulture));
        Append(builder, entry.ActorUserId?.ToString("D", CultureInfo.InvariantCulture));
        Append(builder, entry.ActorEmail);
        Append(builder, entry.ImpersonatorUserId?.ToString("D", CultureInfo.InvariantCulture));
        Append(builder, entry.ActorType);
        Append(builder, entry.Action);
        Append(builder, entry.TargetType);
        Append(builder, entry.TargetId);
        Append(builder, ((int)entry.Outcome).ToString(CultureInfo.InvariantCulture));
        Append(builder, entry.Changes);
        Append(builder, entry.IpAddress);
        Append(builder, entry.UserAgent);
        Append(builder, entry.RequestId);
        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    /// <summary>
    /// The 64-bit Postgres advisory-lock key for a tenant's chain (null org = the platform chain). Derived
    /// from a namespaced SHA-256 of the org id, so it's stable across boots and collision-resistant; a
    /// collision would only over-serialise two tenants' writers, never corrupt a chain.
    /// </summary>
    public static long LockKey(Guid? organizationId)
    {
        Span<byte> input = stackalloc byte[LockNamespace.Length + 16];
        LockNamespace.CopyTo(input);
        // The genesis/platform chain (null org) hashes the namespace alone — a fixed, distinct key.
        if (organizationId is { } org)
        {
            org.TryWriteBytes(input[LockNamespace.Length..]);
        }
        else
        {
            input = input[..LockNamespace.Length];
        }

        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(input, digest);
        return BitConverter.ToInt64(digest);
    }

    private static void Append(StringBuilder builder, string? value)
    {
        builder.Append(value);
        builder.Append(FieldSeparator);
    }
}
