using WireHQ.Domain.Common;
using WireHQ.Shared.Results;

namespace WireHQ.Modules.Orchestration.Domain;

/// <summary>
/// Binds a WireGuard instance to where it is deployed: <c>Local</c> (config-only, the default),
/// <c>Ssh</c> (a registered <see cref="SshTarget"/>), or <c>Agent</c> (later). Kept in the <c>orch</c>
/// schema rather than smeared onto the WireGuard instance, so orchestration concerns stay decoupled
/// from config modelling. One binding per instance. (docs/12-remote-orchestration.md §4/§7)
/// </summary>
public sealed class DeploymentTarget : AggregateRoot, ITenantOwned, IAuditable
{
    public const string DefaultInterfaceName = "wg0";

    private DeploymentTarget()
    {
    }

    private DeploymentTarget(Guid id, Guid organizationId, Guid instanceId)
        : base(id)
    {
        OrganizationId = organizationId;
        InstanceId = instanceId;
        Kind = DeploymentTargetKind.Local;
        InterfaceName = DefaultInterfaceName;
    }

    public Guid OrganizationId { get; private set; }
    public Guid InstanceId { get; private set; }
    public DeploymentTargetKind Kind { get; private set; }
    public Guid? SshTargetId { get; private set; }

    /// <summary>The bound <see cref="Agent"/> when <see cref="Kind"/> is <see cref="DeploymentTargetKind.Agent"/>.</summary>
    public Guid? AgentId { get; private set; }

    /// <summary>Interface key custody for an agent-bound instance (default <see cref="KeyCustody.WireHqManaged"/>).</summary>
    public KeyCustody KeyCustody { get; private set; }

    /// <summary>
    /// When true, a detected config drift on a remote target auto-enqueues a redeploy to re-converge
    /// (opt-in; only meaningful for <c>Ssh</c>/<c>Agent</c>). Off by default — the operator clicks Deploy.
    /// (docs/12 §13 Phase 3, gap #4)
    /// </summary>
    public bool AutoReconverge { get; private set; }

    /// <summary>The wg-quick interface name on the host (e.g. <c>wg0</c>) — the config file + service suffix.</summary>
    public string InterfaceName { get; private set; } = DefaultInterfaceName;

    public DateTimeOffset CreatedAtUtc { get; private set; }
    public Guid? CreatedBy { get; private set; }
    public DateTimeOffset? UpdatedAtUtc { get; private set; }
    public Guid? UpdatedBy { get; private set; }

    public static DeploymentTarget Create(Guid organizationId, Guid instanceId) =>
        new(Guid.CreateVersion7(), organizationId, instanceId);

    /// <summary>Points the instance at an SSH target.</summary>
    public Result BindSsh(Guid sshTargetId, string? interfaceName, bool autoReconverge)
    {
        Kind = DeploymentTargetKind.Ssh;
        SshTargetId = sshTargetId;
        AgentId = null;
        InterfaceName = NormalizeInterfaceName(interfaceName);
        AutoReconverge = autoReconverge;
        return Result.Success();
    }

    /// <summary>Points the instance at an enrolled agent (Pull) — the agent drains its jobs on its next poll.</summary>
    public Result BindAgent(Guid agentId, KeyCustody keyCustody, string? interfaceName, bool autoReconverge)
    {
        Kind = DeploymentTargetKind.Agent;
        AgentId = agentId;
        KeyCustody = keyCustody;
        SshTargetId = null;
        InterfaceName = NormalizeInterfaceName(interfaceName);
        AutoReconverge = autoReconverge;
        return Result.Success();
    }

    /// <summary>Reverts to config-only (Local) — nothing is enacted remotely.</summary>
    public void BindLocal()
    {
        Kind = DeploymentTargetKind.Local;
        SshTargetId = null;
        AgentId = null;
        AutoReconverge = false;
    }

    private static string NormalizeInterfaceName(string? name)
    {
        var trimmed = name?.Trim();
        // wg-quick interface names follow Linux netdev rules (<= 15 chars). Fall back to the default.
        return string.IsNullOrEmpty(trimmed) || trimmed.Length > 15 ? DefaultInterfaceName : trimmed;
    }
}
