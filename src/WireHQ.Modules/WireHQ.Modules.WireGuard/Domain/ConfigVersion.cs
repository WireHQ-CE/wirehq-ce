using WireHQ.Domain.Common;

namespace WireHQ.Modules.WireGuard.Domain;

/// <summary>
/// An immutable, versioned snapshot of a rendered instance/peer configuration. The content is stored
/// encrypted (it embeds key material); a sha256 checksum of the plaintext enables diff/verify.
/// Monotonic <see cref="Version"/> per target. (docs/11-wireguard-module.md §6)
/// </summary>
public sealed class ConfigVersion : Entity, ITenantOwned
{
    private ConfigVersion()
    {
    }

    private ConfigVersion(Guid id, Guid organizationId, ConfigTargetType targetType, Guid targetId, int version, string contentEncrypted, string checksum, Guid? createdBy, string? note)
        : base(id)
    {
        OrganizationId = organizationId;
        TargetType = targetType;
        TargetId = targetId;
        Version = version;
        ContentEncrypted = contentEncrypted;
        Checksum = checksum;
        CreatedBy = createdBy;
        Note = note;
        CreatedAtUtc = DateTimeOffset.UtcNow;
    }

    public Guid OrganizationId { get; private set; }
    public ConfigTargetType TargetType { get; private set; }
    public Guid TargetId { get; private set; }
    public int Version { get; private set; }
    public string Format { get; private set; } = "wg-quick";
    public string ContentEncrypted { get; private set; } = null!;
    public string Checksum { get; private set; } = null!;
    public Guid? CreatedBy { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public string? Note { get; private set; }

    public static ConfigVersion Create(Guid organizationId, ConfigTargetType targetType, Guid targetId, int version, string contentEncrypted, string checksum, Guid? createdBy, string? note = null) =>
        new(Guid.CreateVersion7(), organizationId, targetType, targetId, version, contentEncrypted, checksum, createdBy, note);
}
