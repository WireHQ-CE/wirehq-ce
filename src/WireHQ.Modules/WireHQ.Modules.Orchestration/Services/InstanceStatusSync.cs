using Microsoft.EntityFrameworkCore;
using WireHQ.Application.Abstractions;
using WireHQ.Application.Abstractions.Persistence;
using WireHQ.Application.Abstractions.Security;
using WireHQ.Modules.Orchestration.Domain;
using WireHQ.Modules.WireGuard.Domain;
using WireHQ.Modules.WireGuard.Providers;
using WireHQ.Shared.Results;

namespace WireHQ.Modules.Orchestration.Services;

/// <summary>Live status for one instance: telemetry, config-drift verdict, and whether the bound provider exposes live status.</summary>
public sealed record InstanceLiveStatus(
    bool HasLiveStatus,
    ProviderInstanceState State,
    int? ListenPort,
    DateTimeOffset ObservedAtUtc,
    IReadOnlyList<ProviderPeerStatus> Peers,
    bool HasDrift,
    string? DriftDetail);

/// <summary>Pulls live status + drift for an instance through its bound provider and persists the result.</summary>
public interface IInstanceStatusSync
{
    /// <summary>
    /// Resolves the instance's deployment binding, probes its provider for live status <b>and config
    /// drift</b> (re-render desired vs the deployed config), then persists telemetry
    /// (<see cref="Peer.UpdateTelemetry"/>), the instance state (drift ⇒ <c>Degraded</c>), and an
    /// <see cref="InstanceRuntimeStatus"/> row. The caller owns <c>SaveChanges</c> + the tenant scope.
    /// Local/unbound instances report no live status without touching the host or the DB.
    /// </summary>
    Task<Result<InstanceLiveStatus>> SyncAsync(Guid instanceId, CancellationToken cancellationToken);
}

/// <summary>
/// The single place that turns a provider's status + drift into persisted instance state, peer telemetry,
/// and a runtime-status row — reused by the on-demand status query and the periodic reconciler so both
/// behave identically. Provider-resolution mirrors <c>JobDispatcher</c>: the deployment binding (orch),
/// not the instance's <c>ProviderType</c>, decides which provider answers. (docs/12-remote-orchestration.md §10)
/// </summary>
public sealed class InstanceStatusSync(
    IApplicationDbContext dbContext,
    IWireGuardProviderFactory providerFactory,
    IServerConfigRenderer renderer,
    ISecretProtector secretProtector,
    IAutoReconverger autoReconverger,
    IDateTimeProvider clock)
    : IInstanceStatusSync
{
    public async Task<Result<InstanceLiveStatus>> SyncAsync(Guid instanceId, CancellationToken cancellationToken)
    {
        var instance = await dbContext.Set<WireGuardInstance>()
            .FirstOrDefaultAsync(i => i.Id == instanceId, cancellationToken);
        if (instance is null)
        {
            return OrchestrationErrors.Deployment.InstanceNotFound;
        }

        var binding = await dbContext.Set<DeploymentTarget>()
            .FirstOrDefaultAsync(t => t.InstanceId == instanceId, cancellationToken);
        var kind = binding?.Kind ?? DeploymentTargetKind.Local;

        // Only SSH targets expose live status today; Local/Agent report "no live status" without a probe.
        if (kind != DeploymentTargetKind.Ssh)
        {
            return new InstanceLiveStatus(HasLiveStatus: false, ProviderInstanceState.Unknown, ListenPort: null, clock.UtcNow, Peers: [], HasDrift: false, DriftDetail: null);
        }

        var sshTarget = binding!.SshTargetId is { } id
            ? await dbContext.Set<SshTarget>().FirstOrDefaultAsync(t => t.Id == id, cancellationToken)
            : null;
        if (sshTarget is null)
        {
            return OrchestrationErrors.Target.SshTargetRequired;
        }

        var provider = providerFactory.Resolve(WireGuardProviderType.SshLinux);
        var providerRef = new ProviderInstanceRef(instance.Id, instance.ExternalId, new Dictionary<string, string>
        {
            ["host"] = sshTarget.Host,
            ["port"] = sshTarget.Port.ToString(),
            ["username"] = sshTarget.Username,
            ["authKind"] = sshTarget.AuthKind.ToString(),
            ["credential"] = secretProtector.Unprotect(sshTarget.CredentialCiphertext),
            ["hostKeyFingerprint"] = sshTarget.HostKeyFingerprint ?? string.Empty,
            ["interfaceName"] = binding.InterfaceName,
        });

        var statusResult = await provider.GetInstanceStatusAsync(providerRef, cancellationToken);
        if (statusResult.IsFailure)
        {
            return statusResult.Error;
        }

        var status = statusResult.Value;
        var drift = await DetectDriftAsync(provider, providerRef, instance, binding.InterfaceName, binding.KeyCustody, cancellationToken);

        await PersistAsync(instance, status, drift, cancellationToken);

        // Opt-in remediation: a drifted target with auto-re-converge enabled gets a redeploy enqueued.
        if (binding.AutoReconverge && (drift?.HasDrift ?? false))
        {
            await autoReconverger.TryEnqueueAsync(instance.OrganizationId, instance.Id, cancellationToken);
        }

        return new InstanceLiveStatus(
            HasLiveStatus: provider.Capabilities.HasFlag(ProviderCapabilities.LiveStatus),
            status.State, status.ListenPort, status.ObservedAtUtc, status.Peers,
            HasDrift: drift?.HasDrift ?? false, DriftDetail: drift?.Detail);
    }

    // Best-effort: render desired + ask the provider to compare it to the deployed config. A render or
    // probe failure leaves drift unknown (null) rather than failing the whole status pull.
    private async Task<ConfigDrift?> DetectDriftAsync(
        IWireGuardProvider provider, ProviderInstanceRef providerRef, WireGuardInstance instance, string interfaceName, KeyCustody keyCustody, CancellationToken cancellationToken)
    {
        if (!provider.Capabilities.HasFlag(ProviderCapabilities.DriftDetection))
        {
            return null;
        }

        var rendered = await renderer.RenderAsync(instance, interfaceName, keyCustody, cancellationToken);
        if (rendered.IsFailure)
        {
            return null;
        }

        var drift = await provider.GetConfigDriftAsync(providerRef, rendered.Value, cancellationToken);
        return drift.IsSuccess ? drift.Value : null;
    }

    // Mutates tracked entities; the caller saves. Drift takes precedence in the headline state — a
    // drifted gateway reads as Degraded even if the interface is up.
    private async Task PersistAsync(WireGuardInstance instance, ProviderInstanceStatus status, ConfigDrift? drift, CancellationToken cancellationToken)
    {
        var hasDrift = drift?.HasDrift ?? false;
        if (hasDrift)
        {
            instance.ChangeStatus(InstanceStatus.Degraded, clock.UtcNow);
        }
        else if (status.State != ProviderInstanceState.Unknown)
        {
            instance.ChangeStatus(MapState(status.State), clock.UtcNow);
        }

        await UpsertRuntimeStatusAsync(instance, status, drift, cancellationToken);
        await ApplyPeerTelemetryAsync(instance, status, cancellationToken);
    }

    private async Task UpsertRuntimeStatusAsync(WireGuardInstance instance, ProviderInstanceStatus status, ConfigDrift? drift, CancellationToken cancellationToken)
    {
        var runtime = await dbContext.Set<InstanceRuntimeStatus>()
            .FirstOrDefaultAsync(r => r.InstanceId == instance.Id, cancellationToken);
        if (runtime is null)
        {
            runtime = InstanceRuntimeStatus.Create(instance.OrganizationId, instance.Id);
            dbContext.Set<InstanceRuntimeStatus>().Add(runtime);
        }

        runtime.Record(status.State.ToString(), drift?.DesiredHash, drift?.ActualHash, drift?.HasDrift ?? false, drift?.Detail, clock.UtcNow);
    }

    private async Task ApplyPeerTelemetryAsync(WireGuardInstance instance, ProviderInstanceStatus status, CancellationToken cancellationToken)
    {
        if (status.Peers.Count == 0)
        {
            return;
        }

        var observed = status.Peers.ToDictionary(p => p.PublicKey, StringComparer.Ordinal);
        var peers = await dbContext.Set<Peer>()
            .Where(p => p.InstanceId == instance.Id)
            .ToListAsync(cancellationToken);

        foreach (var peer in peers)
        {
            if (observed.TryGetValue(peer.PublicKey, out var t))
            {
                peer.UpdateTelemetry(t.LastHandshakeAt, t.RxBytes, t.TxBytes, t.Endpoint, clock.UtcNow);
            }
        }
    }

    private static InstanceStatus MapState(ProviderInstanceState state) => state switch
    {
        ProviderInstanceState.Running => InstanceStatus.Running,
        ProviderInstanceState.Stopped => InstanceStatus.Stopped,
        ProviderInstanceState.Degraded => InstanceStatus.Degraded,
        ProviderInstanceState.Error => InstanceStatus.Error,
        _ => InstanceStatus.Created,
    };
}
