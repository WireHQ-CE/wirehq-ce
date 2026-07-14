using WireHQ.Domain.Common;
using WireHQ.Shared.Results;

namespace WireHQ.Modules.Orchestration.Domain;

/// <summary>
/// A remote Linux host WireHQ can deploy a WireGuard config to over SSH. Holds connection details and
/// an <b>encrypted</b> credential (private key PEM or password — via <c>ISecretProtector</c>, never
/// returned on read) plus the pinned host-key fingerprint (no blind TOFU). Tenant-owned, audited,
/// soft-deletable. The <c>SshWireGuardProvider</c> (Phase 1 slice 2) uses these to push deploys.
/// (docs/12-remote-orchestration.md §6/§7)
/// </summary>
public sealed class SshTarget : AggregateRoot, ITenantOwned, IAuditable, ISoftDeletable
{
    public const int MaxNameLength = 96;
    public const int DefaultPort = 22;

    private SshTarget()
    {
    }

    private SshTarget(
        Guid id, Guid organizationId, string name, string host, int port, string username,
        SshAuthKind authKind, string credentialCiphertext, string? hostKeyFingerprint)
        : base(id)
    {
        OrganizationId = organizationId;
        Name = name;
        Host = host;
        Port = port;
        Username = username;
        AuthKind = authKind;
        CredentialCiphertext = credentialCiphertext;
        HostKeyFingerprint = hostKeyFingerprint;
    }

    public Guid OrganizationId { get; private set; }
    public string Name { get; private set; } = null!;
    public string Host { get; private set; } = null!;
    public int Port { get; private set; }
    public string Username { get; private set; } = null!;
    public SshAuthKind AuthKind { get; private set; }

    /// <summary>The private key or password, encrypted at rest. Never leaves the server in clear.</summary>
    public string CredentialCiphertext { get; private set; } = null!;

    /// <summary>Pinned SSH host-key fingerprint; the provider refuses to connect to a mismatched host.</summary>
    public string? HostKeyFingerprint { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }
    public Guid? CreatedBy { get; private set; }
    public DateTimeOffset? UpdatedAtUtc { get; private set; }
    public Guid? UpdatedBy { get; private set; }
    public bool IsDeleted { get; private set; }
    public DateTimeOffset? DeletedAtUtc { get; private set; }
    public Guid? DeletedBy { get; private set; }

    public static Result<SshTarget> Create(
        Guid id, Guid organizationId, string name, string host, int? port, string username,
        SshAuthKind authKind, string credentialCiphertext, string? hostKeyFingerprint)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > MaxNameLength)
        {
            return OrchestrationErrors.SshTarget.InvalidName;
        }

        if (string.IsNullOrWhiteSpace(host))
        {
            return OrchestrationErrors.SshTarget.InvalidHost;
        }

        var resolvedPort = port ?? DefaultPort;
        if (resolvedPort is < 1 or > 65535)
        {
            return OrchestrationErrors.SshTarget.InvalidPort;
        }

        if (string.IsNullOrWhiteSpace(username))
        {
            return OrchestrationErrors.SshTarget.InvalidUsername;
        }

        return new SshTarget(
            id, organizationId, name.Trim(), host.Trim(), resolvedPort, username.Trim(),
            authKind, credentialCiphertext, NormalizeFingerprint(hostKeyFingerprint));
    }

    public Result Rename(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > MaxNameLength)
        {
            return OrchestrationErrors.SshTarget.InvalidName;
        }

        Name = name.Trim();
        return Result.Success();
    }

    public Result UpdateConnection(string host, int? port, string username)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return OrchestrationErrors.SshTarget.InvalidHost;
        }

        var resolvedPort = port ?? Port;
        if (resolvedPort is < 1 or > 65535)
        {
            return OrchestrationErrors.SshTarget.InvalidPort;
        }

        if (string.IsNullOrWhiteSpace(username))
        {
            return OrchestrationErrors.SshTarget.InvalidUsername;
        }

        Host = host.Trim();
        Port = resolvedPort;
        Username = username.Trim();
        return Result.Success();
    }

    /// <summary>Replaces the stored credential (already encrypted by the caller).</summary>
    public void RotateCredential(SshAuthKind authKind, string credentialCiphertext)
    {
        AuthKind = authKind;
        CredentialCiphertext = credentialCiphertext;
    }

    public void SetHostKeyFingerprint(string? fingerprint) => HostKeyFingerprint = NormalizeFingerprint(fingerprint);

    private static string? NormalizeFingerprint(string? fingerprint) =>
        string.IsNullOrWhiteSpace(fingerprint) ? null : fingerprint.Trim();
}
