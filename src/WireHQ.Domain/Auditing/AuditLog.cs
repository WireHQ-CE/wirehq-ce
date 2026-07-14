using WireHQ.Domain.Common;

namespace WireHQ.Domain.Auditing;

public enum AuditOutcome
{
    Success = 0,
    Failure = 1,
}

/// <summary>
/// An immutable record of a security-relevant or mutating action. Append-only: the
/// application never updates or deletes audit rows (DB grants enforce it too), and entries are
/// written on the domain-event/outbox path so they stay consistent with the action they
/// describe. (docs/04-security.md)
/// </summary>
public sealed class AuditLog : Entity
{
    // EF Core
    private AuditLog()
    {
    }

    private AuditLog(Guid id)
        : base(id)
    {
    }

    /// <summary>Tenant the action occurred in. Null for platform-scoped actions.</summary>
    public Guid? OrganizationId { get; private set; }

    /// <summary>The acting principal (user or service). Null for anonymous/system actions.</summary>
    public Guid? ActorUserId { get; private set; }

    /// <summary>The actor's email snapshotted at write time, so the record survives user deletion (docs/15 §5).</summary>
    public string? ActorEmail { get; private set; }

    /// <summary>When the action was performed under impersonation, the platform operator who was acting.</summary>
    public Guid? ImpersonatorUserId { get; private set; }

    public string ActorType { get; private set; } = "user";

    /// <summary>The permission/action key, e.g. <c>identity.users.invite</c>.</summary>
    public string Action { get; private set; } = null!;

    public string? TargetType { get; private set; }

    public string? TargetId { get; private set; }

    public AuditOutcome Outcome { get; private set; }

    /// <summary>Structured before/after diff (JSON). Null for non-mutating events.</summary>
    public string? Changes { get; private set; }

    public string? IpAddress { get; private set; }

    public string? UserAgent { get; private set; }

    public string? RequestId { get; private set; }

    public DateTimeOffset OccurredAtUtc { get; private set; }

    /// <summary>
    /// Tamper-evidence (ADR-031). The previous entry's <see cref="EntryHash"/> in this tenant's chain
    /// (null for the genesis entry, and for rows written before the hash-chain migration). Set by the
    /// persistence layer under a per-tenant advisory lock — never by call sites. (docs/15 §5)
    /// </summary>
    public byte[]? PrevHash { get; private set; }

    /// <summary>This entry's hash = SHA-256(prev_hash ‖ canonical(entry)). Null only for legacy un-chained rows.</summary>
    public byte[]? EntryHash { get; private set; }

    /// <summary>
    /// Links this entry into its tenant's hash chain. Called once, by the persistence layer, after the
    /// chain head for the tenant has been read under the advisory lock. Idempotent inputs in, immutable out.
    /// </summary>
    public void ApplyChain(byte[]? prevHash, byte[] entryHash)
    {
        PrevHash = prevHash;
        EntryHash = entryHash;
    }

    public static AuditLog Record(
        string action,
        AuditOutcome outcome,
        Guid? organizationId = null,
        Guid? actorUserId = null,
        string actorType = "user",
        string? targetType = null,
        string? targetId = null,
        string? changes = null,
        string? ipAddress = null,
        string? userAgent = null,
        string? requestId = null,
        string? actorEmail = null,
        Guid? impersonatorUserId = null,
        DateTimeOffset? occurredAtUtc = null) =>
        new(Guid.CreateVersion7())
        {
            Action = action,
            Outcome = outcome,
            OrganizationId = organizationId,
            ActorUserId = actorUserId,
            ActorEmail = actorEmail,
            ImpersonatorUserId = impersonatorUserId,
            ActorType = actorType,
            TargetType = targetType,
            TargetId = targetId,
            Changes = changes,
            IpAddress = ipAddress,
            UserAgent = userAgent,
            RequestId = requestId,
            OccurredAtUtc = occurredAtUtc ?? DateTimeOffset.UtcNow,
        };
}
