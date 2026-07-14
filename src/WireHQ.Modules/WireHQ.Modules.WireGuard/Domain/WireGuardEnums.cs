namespace WireHQ.Modules.WireGuard.Domain;

public enum InstanceStatus
{
    Created = 0,
    Running = 1,
    Stopped = 2,
    Degraded = 3,
    Error = 4,
}

public enum PeerStatus
{
    Pending = 0,
    Active = 1,
    Disabled = 2,
    Revoked = 3,
}

public enum KeyOwnerType
{
    Instance = 0,
    Peer = 1,
}

public enum KeyKind
{
    Private = 0,
    Preshared = 1,
}

public enum KeyStatus
{
    Active = 0,
    Rotating = 1,
    Revoked = 2,
}

public enum ConfigTargetType
{
    Instance = 0,
    Peer = 1,
}

public enum EnrollmentBatchStatus
{
    Validating = 0,
    Previewed = 1,
    Importing = 2,
    Completed = 3,
    Failed = 4,
}
