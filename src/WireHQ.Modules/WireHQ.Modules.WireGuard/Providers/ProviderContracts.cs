namespace WireHQ.Modules.WireGuard.Providers;

// Provider-neutral DTOs. No EF entities cross the provider boundary, so a remote provider that
// holds its own identifiers (via ExternalId) behaves identically to the local one.

/// <summary>Desired state of an instance/interface, materialized (secrets decrypted) for the provider.</summary>
public sealed record ProvisionInstance(
    Guid InstanceId,
    string Name,
    int ListenPort,
    string InterfaceAddress,
    string PrivateKey,
    IReadOnlyList<string> Dns,
    int Mtu,
    string? EndpointHost,
    IReadOnlyDictionary<string, string> ProviderSettings);

/// <summary>A handle to an instance that already exists on a provider.</summary>
public sealed record ProviderInstanceRef(
    Guid InstanceId,
    string? ExternalId,
    IReadOnlyDictionary<string, string> ProviderSettings);

/// <summary>Result of provisioning — carries any opaque id the provider assigned.</summary>
public sealed record ProviderInstanceResult(string? ExternalId);

/// <summary>Desired state of a single peer to apply to an instance.</summary>
public sealed record ProviderPeerSpec(
    string PublicKey,
    string? PresharedKey,
    IReadOnlyList<string> AllowedIps,
    string? Endpoint,
    int? PersistentKeepalive);

/// <summary>Live telemetry for a peer (from <c>wg show</c> or a remote API).</summary>
public sealed record ProviderPeerStatus(
    string PublicKey,
    DateTimeOffset? LastHandshakeAt,
    long RxBytes,
    long TxBytes,
    string? Endpoint);

/// <summary>Live status of an instance + its peers (when the provider supports telemetry).</summary>
public sealed record ProviderInstanceStatus(
    ProviderInstanceState State,
    int? ListenPort,
    DateTimeOffset ObservedAtUtc,
    IReadOnlyList<ProviderPeerStatus> Peers);

/// <summary>
/// Whether the deployed config on the target differs from WireHQ's desired config (by checksum), used
/// by the reconciler to surface drift. A provider without drift detection reports
/// <see cref="HasDrift"/> = false. (docs/12-remote-orchestration.md §10)
/// </summary>
public sealed record ConfigDrift(bool HasDrift, string? DesiredHash, string? ActualHash, string? Detail);

/// <summary>
/// A fully-rendered server config to deploy to a target: the wg-quick interface name (e.g. <c>wg0</c>)
/// and the complete <c>[Interface]</c> + <c>[Peer]</c> text. The dispatcher renders this from desired
/// state (decrypting keys just-in-time); the provider only transports + applies it, so providers stay
/// free of WireGuard internals. (docs/12-remote-orchestration.md §4)
/// </summary>
public sealed record RenderedServerConfig(string InterfaceName, string ConfigText);
