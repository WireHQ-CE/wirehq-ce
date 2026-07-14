namespace WireHQ.Modules.Orchestration.Domain;

/// <summary>
/// A deployment job's lifecycle. <c>Pending</c> → claimed (<c>Dispatched</c>) → enacting
/// (<c>Applying</c>) → terminal (<c>Succeeded</c> / <c>Failed</c> / <c>RolledBack</c>).
/// (docs/12-remote-orchestration.md §4, ADR-018)
/// </summary>
public enum DeploymentJobStatus
{
    Pending = 0,
    Dispatched = 1,
    Applying = 2,
    Succeeded = 3,
    Failed = 4,
    RolledBack = 5,
}

/// <summary>What a job asks the target to do. Only <c>DeployConfig</c> is exercised in Phase 0.</summary>
public enum DeploymentJobType
{
    DeployConfig = 0,
    ApplyPeer = 1,
    RemovePeer = 2,
    Control = 3,
    RotateKeys = 4,
    RemoveInstance = 5,
}

/// <summary>How an SSH target authenticates: a private key (preferred) or a password.</summary>
public enum SshAuthKind
{
    PrivateKey = 0,
    Password = 1,
}

/// <summary>Where an instance is deployed. <c>Local</c> = config-only (no remote enactment).</summary>
public enum DeploymentTargetKind
{
    Local = 0,
    Ssh = 1,
    Agent = 2,
}

/// <summary>
/// Who holds an agent-bound instance's interface private key. <c>WireHqManaged</c> (default for now): WireHQ
/// generates + encrypts the server key and ships it in the signed bundle (full server-config/QR export stays
/// available). <c>AgentManaged</c> (the agent generates the key locally, returns only the public key; WireHQ
/// never holds it) is wired up in Slice D. (ADR-020/028)
/// </summary>
public enum KeyCustody
{
    WireHqManaged = 0,
    AgentManaged = 1,
}
