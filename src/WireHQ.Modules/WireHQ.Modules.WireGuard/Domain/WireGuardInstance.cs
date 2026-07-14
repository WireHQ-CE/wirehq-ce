using WireHQ.Domain.Common;
using WireHQ.Domain.ValueObjects;
using WireHQ.Modules.WireGuard.Providers;
using WireHQ.Shared.Results;

namespace WireHQ.Modules.WireGuard.Domain;

public sealed record WireGuardInstanceCreated(Guid InstanceId, Guid OrganizationId, string Name) : IDomainEvent;

public sealed record WireGuardInstanceStatusChanged(Guid InstanceId, InstanceStatus Status) : IDomainEvent;

public sealed record WireGuardInstanceDeleted(Guid InstanceId, Guid OrganizationId) : IDomainEvent;

/// <summary>
/// A WireGuard interface/tunnel ("server"). Holds the public key + a reference to its
/// envelope-encrypted private key (<see cref="PrivateKeyId"/> → KeyMaterial). The
/// <see cref="ProviderType"/> selects which data plane enacts it. Tenant-owned, audited, soft-deletable.
/// </summary>
public sealed class WireGuardInstance : AggregateRoot, ITenantOwned, IAuditable, ISoftDeletable
{
    public const int MaxNameLength = 96;
    public const int DefaultMtu = 1420;

    private WireGuardInstance()
    {
    }

    private WireGuardInstance(
        Guid id, Guid organizationId, Guid networkId, string name, Slug slug,
        WireGuardProviderType providerType, int listenPort, string interfaceAddress,
        string publicKey, Guid privateKeyId)
        : base(id)
    {
        OrganizationId = organizationId;
        NetworkId = networkId;
        Name = name;
        Slug = slug;
        ProviderType = providerType;
        ListenPort = listenPort;
        InterfaceAddress = interfaceAddress;
        PublicKey = publicKey;
        PrivateKeyId = privateKeyId;
        Status = InstanceStatus.Created;
        Mtu = DefaultMtu;
    }

    public Guid OrganizationId { get; private set; }
    public Guid NetworkId { get; private set; }
    public string Name { get; private set; } = null!;
    public Slug Slug { get; private set; } = null!;
    public string? Description { get; private set; }
    public WireGuardProviderType ProviderType { get; private set; }
    public int ListenPort { get; private set; }
    public string InterfaceAddress { get; private set; } = null!;
    public string PublicKey { get; private set; } = null!;

    /// <summary>
    /// The instance's envelope-encrypted interface private key (→ <c>KeyMaterial</c>). <c>null</c> for an
    /// <c>AgentManaged</c> instance whose key the agent generated and reported — WireHQ then holds only the
    /// public key and cannot render a full server config. (ADR-028)
    /// </summary>
    public Guid? PrivateKeyId { get; private set; }
    public IReadOnlyCollection<string> Dns { get; private set; } = [];
    public string? EndpointHost { get; private set; }
    public int Mtu { get; private set; }
    public InstanceStatus Status { get; private set; }
    public string? ExternalId { get; private set; }
    public IReadOnlyDictionary<string, string> ProviderSettings { get; private set; } = new Dictionary<string, string>();
    public DateTimeOffset? LastStatusAtUtc { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }
    public Guid? CreatedBy { get; private set; }
    public DateTimeOffset? UpdatedAtUtc { get; private set; }
    public Guid? UpdatedBy { get; private set; }
    public bool IsDeleted { get; private set; }
    public DateTimeOffset? DeletedAtUtc { get; private set; }
    public Guid? DeletedBy { get; private set; }

    public static Result<WireGuardInstance> Create(
        Guid id, Guid organizationId, Guid networkId, string name, string? slug,
        WireGuardProviderType providerType, int listenPort, string interfaceAddress,
        string publicKey, Guid privateKeyId)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > MaxNameLength)
        {
            return WireGuardErrors.Instance.InvalidName;
        }

        if (listenPort is < 1 or > 65535)
        {
            return WireGuardErrors.Instance.InvalidPort;
        }

        if (string.IsNullOrWhiteSpace(interfaceAddress))
        {
            return WireGuardErrors.Instance.InvalidAddress;
        }

        var slugResult = string.IsNullOrWhiteSpace(slug) ? Slug.FromName(name) : Slug.Create(slug);
        if (slugResult.IsFailure)
        {
            return slugResult.Error;
        }

        var instance = new WireGuardInstance(
            id, organizationId, networkId, name.Trim(), slugResult.Value,
            providerType, listenPort, interfaceAddress.Trim(), publicKey, privateKeyId);

        instance.Raise(new WireGuardInstanceCreated(instance.Id, organizationId, instance.Name));
        return instance;
    }

    public Result Rename(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > MaxNameLength)
        {
            return WireGuardErrors.Instance.InvalidName;
        }

        Name = name.Trim();
        return Result.Success();
    }

    public void Describe(string? description) => Description = description?.Trim();

    public void SetEndpoint(string? endpointHost) => EndpointHost = endpointHost?.Trim();

    // Trim + drop blanks so stray spaces (e.g. from "1.1.1.1, 9.9.9.9") never reach the rendered config.
    public void SetDns(IEnumerable<string> dns) =>
        Dns = dns.Select(d => d.Trim()).Where(d => d.Length > 0).ToArray();

    public void SetMtu(int mtu) => Mtu = mtu is >= 576 and <= 9000 ? mtu : DefaultMtu;

    public void SetExternalId(string? externalId) => ExternalId = externalId;

    /// <summary>
    /// Adopts an agent-generated interface key: keep only the public key and drop the WireHQ-held private
    /// key reference. The caller scrubs the now-orphaned <c>KeyMaterial</c>. (ADR-028, <c>AgentManaged</c>)
    /// </summary>
    public Result AdoptAgentManagedKey(string publicKey)
    {
        if (string.IsNullOrWhiteSpace(publicKey))
        {
            return WireGuardErrors.Key.NotFound;
        }

        PublicKey = publicKey.Trim();
        PrivateKeyId = null;
        return Result.Success();
    }

    /// <summary>
    /// Re-keys the instance back under WireHQ custody (e.g. on binding away from an <c>AgentManaged</c>
    /// agent): adopt a freshly-generated WireHQ-held keypair so deploy/export work again.
    /// </summary>
    public void AdoptWireHqManagedKey(string publicKey, Guid privateKeyId)
    {
        PublicKey = publicKey.Trim();
        PrivateKeyId = privateKeyId;
    }

    public void ChangeStatus(InstanceStatus status, DateTimeOffset nowUtc)
    {
        if (Status == status)
        {
            return;
        }

        Status = status;
        LastStatusAtUtc = nowUtc;
        Raise(new WireGuardInstanceStatusChanged(Id, status));
    }

    public void MarkDeleted() => Raise(new WireGuardInstanceDeleted(Id, OrganizationId));
}
