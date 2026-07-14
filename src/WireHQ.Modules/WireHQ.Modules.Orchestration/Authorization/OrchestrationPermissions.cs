using WireHQ.Application.Authorization;

namespace WireHQ.Modules.Orchestration.Authorization;

/// <summary>
/// The orchestration module's permission keys + the contributor that registers them into the platform
/// catalog (the seeder + Owner-role provisioning pick them up automatically). Deployment read/run reuse
/// the WireGuard instance permissions; SSH-target management gets its own keys.
/// (docs/12-remote-orchestration.md §8)
/// </summary>
public static class OrchestrationPermissions
{
    public static class Targets
    {
        public const string Read = "orch.targets.read";
        public const string Manage = "orch.targets.manage";
    }

    public static class Agents
    {
        public const string Read = "orch.agents.read";
        public const string Manage = "orch.agents.manage";
    }

    public static readonly IReadOnlyList<PermissionDefinition> All =
    [
        new(Targets.Read, "Orchestration", "View SSH/remote deployment targets"),
        new(Targets.Manage, "Orchestration", "Create, update and delete deployment targets"),
        new(Agents.Read, "Orchestration", "View enrolled agents and their status"),
        new(Agents.Manage, "Orchestration", "Mint enrollment tokens; disable or revoke agents"),
    ];
}

/// <summary>Registers the module's permissions into the global catalog.</summary>
public sealed class OrchestrationPermissionContributor : IPermissionContributor
{
    public IReadOnlyList<PermissionDefinition> Permissions => OrchestrationPermissions.All;
}
