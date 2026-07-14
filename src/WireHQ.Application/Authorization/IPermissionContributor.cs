namespace WireHQ.Application.Authorization;

/// <summary>
/// Lets a feature module add its permissions to the global catalog. The permission seeder unions
/// the core <see cref="Permissions.All"/> with every registered contributor, so module permissions
/// (e.g. <c>wg.peers.manage</c>) are seeded and grantable without touching core. Code still checks
/// permissions by stable key. (docs/04-security.md, docs/11-wireguard-module.md §8)
/// </summary>
public interface IPermissionContributor
{
    IReadOnlyList<PermissionDefinition> Permissions { get; }
}
