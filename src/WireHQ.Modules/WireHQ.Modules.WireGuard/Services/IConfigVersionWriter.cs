using WireHQ.Modules.WireGuard.Domain;

namespace WireHQ.Modules.WireGuard.Services;

/// <summary>
/// Appends immutable, versioned snapshots of a rendered configuration. Content is stored encrypted
/// (it embeds key material) and checksummed for diff/verify; the version is monotonic per target.
/// Called from the command handlers that produce config (instance/peer create, key rotation, peer
/// update). It never calls SaveChanges — the UnitOfWork behavior persists the appended row as part
/// of the command's transaction. (docs/11-wireguard-module.md §6)
/// </summary>
public interface IConfigVersionWriter
{
    /// <summary>Writes the next version for a target and returns the assigned version number.</summary>
    Task<int> WriteAsync(ConfigTargetType targetType, Guid targetId, string plaintextConfig, string? note, CancellationToken cancellationToken);
}
