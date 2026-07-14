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

    /// <summary>Replaces the certificate (and its fingerprint) when the agent rotates before expiry.</summary>
    public void RotateCertificate(string certificateFingerprint, string certificatePem)
    {
        CertificateFingerprint = certificateFingerprint;
        CertificatePem = certificatePem;
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
