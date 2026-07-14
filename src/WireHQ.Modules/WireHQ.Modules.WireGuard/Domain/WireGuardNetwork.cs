using WireHQ.Domain.Common;
using WireHQ.Shared.Results;

namespace WireHQ.Modules.WireGuard.Domain;

/// <summary>
/// A logical address space that peer IPs are allocated from (e.g. <c>10.8.0.0/24</c>). One or more
/// instances draw from a network. Tenant-owned.
/// </summary>
public sealed class WireGuardNetwork : AggregateRoot, ITenantOwned, IAuditable, ISoftDeletable
{
    public const int MaxNameLength = 96;

    private WireGuardNetwork()
    {
    }

    private WireGuardNetwork(Guid id, Guid organizationId, string name, string cidr)
        : base(id)
    {
        OrganizationId = organizationId;
        Name = name;
        Cidr = cidr;
    }

    public Guid OrganizationId { get; private set; }

    public string Name { get; private set; } = null!;

    /// <summary>CIDR of the address pool, e.g. 10.8.0.0/24.</summary>
    public string Cidr { get; private set; } = null!;

    public IReadOnlyCollection<string> Dns { get; private set; } = [];

    public IReadOnlyCollection<string> DefaultAllowedIps { get; private set; } = ["0.0.0.0/0", "::/0"];

    public DateTimeOffset CreatedAtUtc { get; private set; }
    public Guid? CreatedBy { get; private set; }
    public DateTimeOffset? UpdatedAtUtc { get; private set; }
    public Guid? UpdatedBy { get; private set; }
    public bool IsDeleted { get; private set; }
    public DateTimeOffset? DeletedAtUtc { get; private set; }
    public Guid? DeletedBy { get; private set; }

    public static Result<WireGuardNetwork> Create(Guid organizationId, string name, string cidr)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > MaxNameLength)
        {
            return WireGuardErrors.Network.InvalidName;
        }

        if (string.IsNullOrWhiteSpace(cidr))
        {
            return WireGuardErrors.Network.InvalidCidr;
        }

        return new WireGuardNetwork(Guid.CreateVersion7(), organizationId, name.Trim(), cidr.Trim());
    }

    public Result Rename(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > MaxNameLength)
        {
            return WireGuardErrors.Network.InvalidName;
        }

        Name = name.Trim();
        return Result.Success();
    }

    // Trim + drop blanks so a "1.1.1.1, 9.9.9.9" style entry never leaves a stray space in the
    // rendered config (e.g. " 9.9.9.9"), regardless of how the caller split the input.
    public void SetDns(IEnumerable<string> dns) =>
        Dns = dns.Select(d => d.Trim()).Where(d => d.Length > 0).ToArray();

    public void SetDefaultAllowedIps(IEnumerable<string> allowedIps) => DefaultAllowedIps = allowedIps.ToArray();
}
