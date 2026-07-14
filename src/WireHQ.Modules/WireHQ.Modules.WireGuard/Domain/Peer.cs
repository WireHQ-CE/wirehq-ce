using WireHQ.Domain.Common;
using WireHQ.Shared.Results;

namespace WireHQ.Modules.WireGuard.Domain;

public sealed record PeerCreated(Guid PeerId, Guid OrganizationId, Guid InstanceId) : IDomainEvent;

public sealed record PeerStatusChanged(Guid PeerId, PeerStatus Status) : IDomainEvent;

public sealed record PeerRevoked(Guid PeerId, Guid OrganizationId, Guid InstanceId, string PublicKey) : IDomainEvent;

/// <summary>
/// A client/device on an instance. Optionally bound to a WireHQ identity via
/// <see cref="MembershipId"/> — so offboarding a person revokes their tunnels. Holds the peer's
/// public key + optional references to its (encrypted) private key and preshared key. Telemetry
/// (handshake, transfer) is updated from provider status. Tenant-owned, audited, soft-deletable.
/// </summary>
public sealed class Peer : AggregateRoot, ITenantOwned, IAuditable, ISoftDeletable
{
    public const int MaxNameLength = 128;

    private Peer()
    {
    }

    private Peer(
        Guid id, Guid organizationId, Guid instanceId, string name, string? email,
        string publicKey, string assignedAddress, Guid? privateKeyId, Guid? presharedKeyId, Guid? membershipId)
        : base(id)
    {
        OrganizationId = organizationId;
        InstanceId = instanceId;
        Name = name;
        Email = email;
        PublicKey = publicKey;
        AssignedAddress = assignedAddress;
        PrivateKeyId = privateKeyId;
        PresharedKeyId = presharedKeyId;
        MembershipId = membershipId;
        Status = PeerStatus.Active;
    }

    public Guid OrganizationId { get; private set; }
    public Guid InstanceId { get; private set; }
    public Guid? MembershipId { get; private set; }
    public string Name { get; private set; } = null!;
    public string? Email { get; private set; }
    public string? Department { get; private set; }
    public string? DeviceType { get; private set; }
    public PeerStatus Status { get; private set; }
    public string PublicKey { get; private set; } = null!;
    public Guid? PrivateKeyId { get; private set; }
    public Guid? PresharedKeyId { get; private set; }
    public string AssignedAddress { get; private set; } = null!;
    public IReadOnlyCollection<string> AllowedIps { get; private set; } = [];
    public int? PersistentKeepalive { get; private set; }
    public DateTimeOffset? LastHandshakeAtUtc { get; private set; }
    public long RxBytes { get; private set; }
    public long TxBytes { get; private set; }
    public string? LastEndpoint { get; private set; }
    public DateTimeOffset? LastSeenAtUtc { get; private set; }
    public Guid? EnrollmentBatchId { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }
    public Guid? CreatedBy { get; private set; }
    public DateTimeOffset? UpdatedAtUtc { get; private set; }
    public Guid? UpdatedBy { get; private set; }
    public bool IsDeleted { get; private set; }
    public DateTimeOffset? DeletedAtUtc { get; private set; }
    public Guid? DeletedBy { get; private set; }

    public static Result<Peer> Create(
        Guid id, Guid organizationId, Guid instanceId, string name, string? email,
        string publicKey, string assignedAddress, Guid? privateKeyId, Guid? presharedKeyId, Guid? membershipId)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > MaxNameLength)
        {
            return WireGuardErrors.Peer.InvalidName;
        }

        if (string.IsNullOrWhiteSpace(publicKey))
        {
            return WireGuardErrors.Peer.InvalidName;
        }

        var peer = new Peer(
            id, organizationId, instanceId, name.Trim(), email?.Trim(),
            publicKey, assignedAddress, privateKeyId, presharedKeyId, membershipId);

        peer.Raise(new PeerCreated(peer.Id, organizationId, instanceId));
        return peer;
    }

    public void SetProfile(string? department, string? deviceType)
    {
        Department = department?.Trim();
        DeviceType = deviceType?.Trim();
    }

    public void SetAllowedIps(IEnumerable<string> allowedIps) => AllowedIps = allowedIps.ToArray();

    public void SetKeepalive(int? seconds) => PersistentKeepalive = seconds is > 0 ? seconds : null;

    public void SetEnrollmentBatch(Guid batchId) => EnrollmentBatchId = batchId;

    /// <summary>Swaps the peer's key references during a key rotation.</summary>
    public void ReplaceKeys(string publicKey, Guid? privateKeyId, Guid? presharedKeyId)
    {
        PublicKey = publicKey;
        PrivateKeyId = privateKeyId;
        PresharedKeyId = presharedKeyId;
    }

    public Result Rename(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > MaxNameLength)
        {
            return WireGuardErrors.Peer.InvalidName;
        }

        Name = name.Trim();
        return Result.Success();
    }

    public Result Enable()
    {
        if (Status == PeerStatus.Revoked)
        {
            return WireGuardErrors.Peer.Revoked;
        }

        ChangeStatus(PeerStatus.Active);
        return Result.Success();
    }

    public Result Disable()
    {
        if (Status == PeerStatus.Revoked)
        {
            return WireGuardErrors.Peer.Revoked;
        }

        ChangeStatus(PeerStatus.Disabled);
        return Result.Success();
    }

    public void Revoke()
    {
        if (Status == PeerStatus.Revoked)
        {
            return;
        }

        Status = PeerStatus.Revoked;
        Raise(new PeerStatusChanged(Id, PeerStatus.Revoked));
        Raise(new PeerRevoked(Id, OrganizationId, InstanceId, PublicKey));
    }

    /// <summary>Updates live telemetry from provider status (handshake, transfer, endpoint).</summary>
    public void UpdateTelemetry(DateTimeOffset? lastHandshakeAtUtc, long rxBytes, long txBytes, string? endpoint, DateTimeOffset nowUtc)
    {
        LastHandshakeAtUtc = lastHandshakeAtUtc;
        RxBytes = rxBytes;
        TxBytes = txBytes;
        LastEndpoint = endpoint;
        LastSeenAtUtc = nowUtc;
    }

    private void ChangeStatus(PeerStatus status)
    {
        if (Status == status)
        {
            return;
        }

        Status = status;
        Raise(new PeerStatusChanged(Id, status));
    }
}
