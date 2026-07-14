namespace WireHQ.Modules.WireGuard.Providers;

/// <summary>Where a WireGuard instance's data plane lives. Selected per instance.</summary>
public enum WireGuardProviderType
{
    /// <summary>Config/model only — generates and manages configs + keys, no live kernel data plane (default).</summary>
    Local = 0,
    /// <summary>Local kernel data plane via wg/wg-quick (opt-in, privileged gateway container).</summary>
    LocalKernel = 1,
    PfSense = 2,
    OpnSense = 3,
    SshLinux = 4,
    Aws = 5,
    Azure = 6,
    Gcp = 7,
    /// <summary>Outbound-only mTLS agent (Pull) — a host binary drains signed jobs + reports back (ADR-028).</summary>
    Agent = 8,
}

public enum InstanceAction
{
    Start = 0,
    Stop = 1,
    Restart = 2,
}

public enum ProviderInstanceState
{
    Unknown = 0,
    Running = 1,
    Stopped = 2,
    Degraded = 3,
    Error = 4,
}

/// <summary>
/// What a provider can do. The UI and services degrade gracefully against this — e.g. a config-only
/// provider reports no <see cref="LiveStatus"/>, so the dashboard shows "telemetry unavailable"
/// rather than failing. Future providers light up firewall/sync bits.
/// </summary>
[Flags]
public enum ProviderCapabilities
{
    None = 0,
    ManagePeers = 1 << 0,
    ControlInterface = 1 << 1,
    LiveStatus = 1 << 2,
    FirewallRules = 1 << 3,
    FirewallAliases = 1 << 4,
    Sync = 1 << 5,

    // Remote-orchestration capabilities (docs/12-remote-orchestration.md §3).
    RemoteDeploy = 1 << 6,
    Telemetry = 1 << 7,
    DriftDetection = 1 << 8,
}

/// <summary>
/// How a provider's desired state is enacted, so the deployment-job dispatcher knows what to do:
/// <see cref="None"/> = config-only, nothing to enact (the model is the truth);
/// <see cref="Push"/> = WireHQ enacts synchronously (SSH/API); <see cref="Pull"/> = a remote agent
/// enacts on its next outbound poll. (docs/12-remote-orchestration.md §3/§4, ADR-018)
/// </summary>
public enum ProviderExecutionModel
{
    None = 0,
    Push = 1,
    Pull = 2,
}
