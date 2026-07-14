using WireHQ.Shared.Results;

namespace WireHQ.Modules.WireGuard.Providers;

/// <summary>
/// The integration-layer abstraction every WireGuard operation flows through, from day one. The
/// services never branch on provider type — they call this interface and honor
/// <see cref="Capabilities"/>. This is what lets the kernel/pfSense/OPNsense/SSH/cloud data planes
/// slot in later as pure additions. (docs/11-wireguard-module.md §3)
/// </summary>
public interface IWireGuardProvider
{
    WireGuardProviderType Type { get; }

    ProviderCapabilities Capabilities { get; }

    /// <summary>How this provider's desired state is enacted (drives the deployment-job dispatcher).</summary>
    ProviderExecutionModel ExecutionModel { get; }

    /// <summary>Probes that the target is reachable + ready to apply config (e.g. SSH up, <c>wg</c> present).</summary>
    Task<Result> TestConnectivityAsync(ProviderInstanceRef instance, CancellationToken cancellationToken);

    /// <summary>
    /// Reports whether the config actually deployed on the target has drifted from WireHQ's
    /// <paramref name="desired"/> config (by checksum). The caller renders <paramref name="desired"/>
    /// from desired state; the provider reads the actual config off the target and compares.
    /// </summary>
    Task<Result<ConfigDrift>> GetConfigDriftAsync(ProviderInstanceRef instance, RenderedServerConfig desired, CancellationToken cancellationToken);

    /// <summary>
    /// Applies a fully-rendered server config to the target (the desired-state deploy). Push providers
    /// (SSH/pfSense/cloud) write + activate it with rollback on failure; the config-only Local provider
    /// is a no-op success.
    /// </summary>
    Task<Result> DeployConfigAsync(ProviderInstanceRef instance, RenderedServerConfig config, CancellationToken cancellationToken);

    // Instance (interface/tunnel) lifecycle
    Task<Result<ProviderInstanceResult>> CreateInstanceAsync(ProvisionInstance spec, CancellationToken cancellationToken);

    Task<Result> UpdateInstanceAsync(ProviderInstanceRef instance, ProvisionInstance spec, CancellationToken cancellationToken);

    Task<Result> DeleteInstanceAsync(ProviderInstanceRef instance, CancellationToken cancellationToken);

    Task<Result> ControlInstanceAsync(ProviderInstanceRef instance, InstanceAction action, CancellationToken cancellationToken);

    Task<Result<ProviderInstanceStatus>> GetInstanceStatusAsync(ProviderInstanceRef instance, CancellationToken cancellationToken);

    // Peer lifecycle
    Task<Result> ApplyPeerAsync(ProviderInstanceRef instance, ProviderPeerSpec peer, CancellationToken cancellationToken);

    Task<Result> RemovePeerAsync(ProviderInstanceRef instance, string publicKey, CancellationToken cancellationToken);

    Task<Result<IReadOnlyList<ProviderPeerStatus>>> GetPeerStatusAsync(ProviderInstanceRef instance, CancellationToken cancellationToken);
}

/// <summary>Resolves the right provider for an instance by its <see cref="WireGuardProviderType"/>.</summary>
public interface IWireGuardProviderFactory
{
    IWireGuardProvider Resolve(WireGuardProviderType type);
}
