using WireHQ.Application.Authorization;

namespace WireHQ.Modules.WireGuard.Authorization;

/// <summary>
/// The module's permission keys (stable strings) + the contributor that registers them into the
/// platform's global catalog. Use cases declare these in <c>RequiredPermissions</c>; the seeder and
/// Owner-role provisioning pick them up automatically. (docs/11-wireguard-module.md §8)
/// </summary>
public static class WireGuardPermissions
{
    public static class Instances
    {
        public const string Read = "wg.instances.read";
        public const string Manage = "wg.instances.manage";
        public const string Export = "wg.instances.export";
    }

    public static class Peers
    {
        public const string Read = "wg.peers.read";
        public const string Manage = "wg.peers.manage";
        public const string Export = "wg.peers.export";
    }

    public static class Keys
    {
        public const string Manage = "wg.keys.manage";
    }

    public static class Enrollment
    {
        public const string Manage = "wg.enrollment.manage";
    }

    public static readonly IReadOnlyList<PermissionDefinition> All =
    [
        new(Instances.Read, "WireGuard", "View WireGuard instances and status"),
        new(Instances.Manage, "WireGuard", "Create, update, control and delete instances"),
        new(Instances.Export, "WireGuard", "Export instance (server) configuration"),
        new(Peers.Read, "WireGuard", "View peers, handshakes and transfer"),
        new(Peers.Manage, "WireGuard", "Create, update, enable/disable and delete peers"),
        new(Peers.Export, "WireGuard", "Export peer configuration and QR codes"),
        new(Keys.Manage, "WireGuard", "Rotate, regenerate and revoke keys"),
        new(Enrollment.Manage, "WireGuard", "Run bulk CSV enrollment"),
    ];
}

/// <summary>Registers the module's permissions into the global catalog.</summary>
public sealed class WireGuardPermissionContributor : IPermissionContributor
{
    public IReadOnlyList<PermissionDefinition> Permissions => WireGuardPermissions.All;
}
