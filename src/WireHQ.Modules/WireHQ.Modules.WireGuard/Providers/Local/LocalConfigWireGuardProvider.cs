using WireHQ.Shared.Results;

namespace WireHQ.Modules.WireGuard.Providers.Local;

/// <summary>
/// The default Phase-1 provider: it manages the <em>desired</em> model + configuration (persisted by
/// the services) but does not touch a kernel data plane, so it is safe in the non-root/read-only API
/// container. Peer apply/remove are accepted into the model; there is no live telemetry. The real
/// data plane is the opt-in <c>LocalKernelWireGuardProvider</c> (a gateway container), which
/// implements the same interface. (docs/11-wireguard-module.md §3.1)
/// </summary>
public sealed class LocalConfigWireGuardProvider : IWireGuardProvider
{
    public WireGuardProviderType Type => WireGuardProviderType.Local;

    public ProviderCapabilities Capabilities => ProviderCapabilities.ManagePeers;

    // Config-only: there is no remote data plane to enact, so a deploy job is a no-op success.
    public ProviderExecutionModel ExecutionModel => ProviderExecutionModel.None;

    public Task<Result> TestConnectivityAsync(ProviderInstanceRef instance, CancellationToken cancellationToken) =>
        // The model lives in-process — always reachable.
        Task.FromResult(Result.Success());

    public Task<Result<ConfigDrift>> GetConfigDriftAsync(ProviderInstanceRef instance, RenderedServerConfig desired, CancellationToken cancellationToken) =>
        // The model is the source of truth — config-only has nothing deployed to drift from.
        Task.FromResult(Result.Success(new ConfigDrift(HasDrift: false, DesiredHash: null, ActualHash: null, Detail: null)));

    public Task<Result> DeployConfigAsync(ProviderInstanceRef instance, RenderedServerConfig config, CancellationToken cancellationToken) =>
        // Config-only: the desired model IS the deployment; nothing to push.
        Task.FromResult(Result.Success());

    public Task<Result<ProviderInstanceResult>> CreateInstanceAsync(ProvisionInstance spec, CancellationToken cancellationToken) =>
        Task.FromResult(Result.Success(new ProviderInstanceResult(ExternalId: null)));

    public Task<Result> UpdateInstanceAsync(ProviderInstanceRef instance, ProvisionInstance spec, CancellationToken cancellationToken) =>
        Task.FromResult(Result.Success());

    public Task<Result> DeleteInstanceAsync(ProviderInstanceRef instance, CancellationToken cancellationToken) =>
        Task.FromResult(Result.Success());

    public Task<Result> ControlInstanceAsync(ProviderInstanceRef instance, InstanceAction action, CancellationToken cancellationToken) =>
        // No live data plane to start/stop. The service/UI gate this on the ControlInterface capability.
        Task.FromResult(Result.Failure(WireGuardProviderErrors.ControlNotSupported));

    public Task<Result<ProviderInstanceStatus>> GetInstanceStatusAsync(ProviderInstanceRef instance, CancellationToken cancellationToken) =>
        // No telemetry — the service falls back to the instance's managed/desired status for display.
        Task.FromResult(Result.Success(new ProviderInstanceStatus(
            ProviderInstanceState.Unknown, ListenPort: null, DateTimeOffset.UtcNow, Peers: [])));

    public Task<Result> ApplyPeerAsync(ProviderInstanceRef instance, ProviderPeerSpec peer, CancellationToken cancellationToken) =>
        Task.FromResult(Result.Success());

    public Task<Result> RemovePeerAsync(ProviderInstanceRef instance, string publicKey, CancellationToken cancellationToken) =>
        Task.FromResult(Result.Success());

    public Task<Result<IReadOnlyList<ProviderPeerStatus>>> GetPeerStatusAsync(ProviderInstanceRef instance, CancellationToken cancellationToken) =>
        Task.FromResult(Result.Success<IReadOnlyList<ProviderPeerStatus>>([]));
}

public static class WireGuardProviderErrors
{
    public static readonly Error ControlNotSupported =
        Error.Conflict("wg.provider.control_unsupported", "This provider cannot start/stop the interface (config-only).");

    public static readonly Error NotRegistered =
        Error.Failure("wg.provider.not_registered", "No WireGuard provider is registered for that type.");
}
