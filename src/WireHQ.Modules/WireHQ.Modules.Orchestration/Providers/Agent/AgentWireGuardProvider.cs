using WireHQ.Modules.WireGuard.Providers;
using WireHQ.Shared.Results;

namespace WireHQ.Modules.Orchestration.Providers.Agent;

/// <summary>
/// The outbound-only mTLS agent data plane (Pull). Unlike the Push providers, it never acts inline: when a
/// job targets an agent-bound instance the dispatcher leaves it <c>Dispatched</c>, and the gateway hands the
/// signed bundle to the agent on its next outbound poll; the agent applies it locally and posts the result.
/// So every method here is a safe no-op — the real enactment happens in the agent + the gateway endpoints.
/// Registered as an <see cref="IWireGuardProvider"/> so the factory resolves it by type. (ADR-028, docs/12 §5)
/// </summary>
public sealed class AgentWireGuardProvider : IWireGuardProvider
{
    public WireGuardProviderType Type => WireGuardProviderType.Agent;

    public ProviderCapabilities Capabilities =>
        ProviderCapabilities.ManagePeers | ProviderCapabilities.RemoteDeploy |
        ProviderCapabilities.Telemetry | ProviderCapabilities.LiveStatus;

    public ProviderExecutionModel ExecutionModel => ProviderExecutionModel.Pull;

    // Connectivity is the agent's own outbound heartbeat — there's nothing for WireHQ to reach inline.
    public Task<Result> TestConnectivityAsync(ProviderInstanceRef instance, CancellationToken cancellationToken) =>
        Task.FromResult(Result.Success());

    // The agent reports drift via telemetry; there is nothing to read inline.
    public Task<Result<ConfigDrift>> GetConfigDriftAsync(ProviderInstanceRef instance, RenderedServerConfig desired, CancellationToken cancellationToken) =>
        Task.FromResult(Result.Success(new ConfigDrift(HasDrift: false, DesiredHash: null, ActualHash: null, Detail: null)));

    // Pull: the dispatcher leaves the job Dispatched for the gateway to deliver — never calls this inline.
    public Task<Result> DeployConfigAsync(ProviderInstanceRef instance, RenderedServerConfig config, CancellationToken cancellationToken) =>
        Task.FromResult(Result.Success());

    public Task<Result<ProviderInstanceResult>> CreateInstanceAsync(ProvisionInstance spec, CancellationToken cancellationToken) =>
        Task.FromResult(Result.Success(new ProviderInstanceResult(ExternalId: null)));

    public Task<Result> UpdateInstanceAsync(ProviderInstanceRef instance, ProvisionInstance spec, CancellationToken cancellationToken) =>
        Task.FromResult(Result.Success());

    public Task<Result> DeleteInstanceAsync(ProviderInstanceRef instance, CancellationToken cancellationToken) =>
        Task.FromResult(Result.Success());

    public Task<Result> ControlInstanceAsync(ProviderInstanceRef instance, InstanceAction action, CancellationToken cancellationToken) =>
        // Start/stop is the agent's local concern; WireHQ doesn't drive the interface inline.
        Task.FromResult(Result.Failure(Error.Conflict(
            "wg.provider.control_unsupported", "This provider cannot start/stop the interface (the agent applies config on its poll).")));

    public Task<Result<ProviderInstanceStatus>> GetInstanceStatusAsync(ProviderInstanceRef instance, CancellationToken cancellationToken) =>
        Task.FromResult(Result.Success(new ProviderInstanceStatus(
            ProviderInstanceState.Unknown, ListenPort: null, DateTimeOffset.UtcNow, Peers: [])));

    public Task<Result> ApplyPeerAsync(ProviderInstanceRef instance, ProviderPeerSpec peer, CancellationToken cancellationToken) =>
        Task.FromResult(Result.Success());

    public Task<Result> RemovePeerAsync(ProviderInstanceRef instance, string publicKey, CancellationToken cancellationToken) =>
        Task.FromResult(Result.Success());

    public Task<Result<IReadOnlyList<ProviderPeerStatus>>> GetPeerStatusAsync(ProviderInstanceRef instance, CancellationToken cancellationToken) =>
        Task.FromResult(Result.Success<IReadOnlyList<ProviderPeerStatus>>([]));
}
