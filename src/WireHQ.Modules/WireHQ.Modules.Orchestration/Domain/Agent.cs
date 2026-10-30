using WireHQ.Domain.Common;
using WireHQ.Shared.Results;

namespace WireHQ.Modules.Orchestration.Domain;

/// <summary>An enrolled agent's lifecycle. Only <see cref="Active"/> agents are accepted by the gateway.</summary>
public enum AgentStatus
{
    /// <summary>Enrolled but awaiting operator approval (reserved; enrollment activates immediately in v1).</summary>
    Pending = 0,
    Active = 1,
    /// <summary>Temporarily suspended by an operator — its cert is rejected; can be re-enabled.</summary>
    Disabled = 2,
    /// <summary>Permanently revoked — its cert is rejected forever.</summary>
    Revoked = 3,
}

/// <summary>
/// An enrolled WireHQ agent: a customer host running the outbound-only Go binary that pulls signed
/// deployment jobs over mTLS and applies WireGuard config locally. Identified by the SHA-256 fingerprint
/// of its (short-lived, WireHQ-issued) client certificate — the gateway maps fingerprint → agent → org →
/// tenant. Revocation is just a status change: the auth handler rejects a non-<see cref="AgentStatus.Active"/>
/// fingerprint immediately (no CRL). Tenant-owned, audited, soft-deletable. (docs/12 §5/§7, ADR-019/028)
/// </summary>
public sealed class Agent : AggregateRoot, ITenantOwned, IAuditable, ISoftDeletable
{
    public const int MaxNameLength = 96;

    private Agent()
    {
    }

    private Agent(
        Guid id, Guid organizationId, string name, string certificateFingerprint,
        string certificatePem, string? platform, DateTimeOffset enrolledAtUtc)
        : base(id)
    {
        OrganizationId = organizationId;
        Name = name;
        CertificateFingerprint = certificateFingerprint;
        CertificatePem = certificatePem;
        Platform = platform;
        Status = AgentStatus.Active;
        EnrolledAtUtc = enrolledAtUtc;
    }

    public Guid OrganizationId { get; private set; }
    public string Name { get; private set; } = null!;

    /// <summary>SHA-256 fingerprint (hex) of the agent's current client certificate — the auth key.</summary>
    public string CertificateFingerprint { get; private set; } = null!;

    /// <summary>The agent's current client certificate (PEM, public). Rotated before expiry.</summary>
    public string CertificatePem { get; private set; } = null!;

    /// <summary>
    /// The fingerprint the agent rotated <i>away from</i>, still accepted until the agent proves it holds the new
    /// certificate (its first heartbeat on it — <see cref="ConfirmCertificate"/>). Without this grace a lost rotate
    /// response, or a crash before the agent persists the new cert, locks the agent out permanently: the server
    /// would have swapped to a certificate the agent never received. Bounded by the old cert's own expiry, which
    /// the auth handler still enforces. Null when no rotation is in flight.
    /// </summary>
    public string? PreviousCertificateFingerprint { get; private set; }

    public AgentStatus Status { get; private set; }
    public string? Platform { get; private set; }
    public string? Version { get; private set; }
    public DateTimeOffset EnrolledAtUtc { get; private set; }
    public DateTimeOffset? LastSeenAtUtc { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }
    public Guid? CreatedBy { get; private set; }
    public DateTimeOffset? UpdatedAtUtc { get; private set; }
    public Guid? UpdatedBy { get; private set; }
    public bool IsDeleted { get; private set; }
    public DateTimeOffset? DeletedAtUtc { get; private set; }
    public Guid? DeletedBy { get; private set; }

    public static Result<Agent> Enroll(
        Guid id, Guid organizationId, string name, string certificateFingerprint,
        string certificatePem, string? platform, DateTimeOffset enrolledAtUtc)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > MaxNameLength)
        {
            return OrchestrationErrors.Agent.InvalidName;
        }

        if (string.IsNullOrWhiteSpace(certificateFingerprint) || string.IsNullOrWhiteSpace(certificatePem))
        {
            return OrchestrationErrors.Agent.InvalidCertificate;
        }

        return new Agent(id, organizationId, name.Trim(), certificateFingerprint, certificatePem,
            string.IsNullOrWhiteSpace(platform) ? null : platform.Trim(), enrolledAtUtc);
    }

    /// <summary>Records a heartbeat — updates the reported version + last-seen time.</summary>
    public void Heartbeat(string? version, DateTimeOffset at)
    {
        Version = string.IsNullOrWhiteSpace(version) ? Version : version.Trim();
        LastSeenAtUtc = at;
    }

    /// <summary>
    /// Replaces the certificate when the agent rotates before expiry. <paramref name="presentedFingerprint"/> is the
    /// cert the rotate request authenticated with. Presenting the <i>current</i> cert is a normal rotation: it
    /// becomes the previous (still accepted) one. Presenting the <i>previous</i> cert means the agent never
    /// received or never persisted the last issued cert — that orphan is discarded and the previous cert keeps its
    /// grace, so the agent can retry indefinitely until one rotation lands.
    /// </summary>
    public void RotateCertificate(string certificateFingerprint, string certificatePem, string presentedFingerprint)
    {
        if (presentedFingerprint == CertificateFingerprint)
        {
            PreviousCertificateFingerprint = CertificateFingerprint;
        }

        CertificateFingerprint = certificateFingerprint;
        CertificatePem = certificatePem;
    }

    /// <summary>
    /// Ends a rotation's grace once the agent authenticates with its new certificate: from then on only the new
    /// cert is accepted. A no-op unless <paramref name="presentedFingerprint"/> is the current certificate.
    /// </summary>
    public void ConfirmCertificate(string presentedFingerprint)
    {
        if (PreviousCertificateFingerprint is not null && presentedFingerprint == CertificateFingerprint)
        {
            PreviousCertificateFingerprint = null;
        }
    }

    /// <summary>
    /// Re-keys the agent through a re-key token: the new certificate replaces the current one outright and any
    /// rotation grace is dropped, so every certificate the agent held before stops working at once. That is what
    /// makes re-keying safe after a suspected compromise, not just after an expiry. A revoked agent is never re-keyed.
    /// </summary>
    public Result Rekey(string certificateFingerprint, string certificatePem)
    {
        if (Status == AgentStatus.Revoked || IsDeleted)
        {
            return OrchestrationErrors.Agent.RekeyNotAllowed;
        }

        if (string.IsNullOrWhiteSpace(certificateFingerprint) || string.IsNullOrWhiteSpace(certificatePem))
        {
            return OrchestrationErrors.Agent.InvalidCertificate;
        }

        CertificateFingerprint = certificateFingerprint;
        CertificatePem = certificatePem;
        PreviousCertificateFingerprint = null;
        return Result.Success();
    }

    public void Disable() => Status = AgentStatus.Disabled;

    public void Reactivate()
    {
        if (Status == AgentStatus.Disabled)
        {
            Status = AgentStatus.Active;
        }
    }

    public void Revoke() => Status = AgentStatus.Revoked;

    /// <summary>True when the agent may authenticate + receive work.</summary>
    public bool IsActive => Status == AgentStatus.Active && !IsDeleted;
}
