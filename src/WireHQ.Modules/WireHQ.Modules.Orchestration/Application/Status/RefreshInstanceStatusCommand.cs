using WireHQ.Application.Common.Messaging;
using WireHQ.Modules.Orchestration.Services;
using WireHQ.Modules.WireGuard.Authorization;
using WireHQ.Shared.Results;

namespace WireHQ.Modules.Orchestration.Application.Status;

/// <summary>
/// Pulls an instance's live status on demand (operator clicked "Refresh") and persists the telemetry, so
/// the peer list repaints from fresh data without waiting for the periodic reconciler. Modelled as a
/// <b>command</b> (not a query) precisely because it writes — the UnitOfWork behavior commits the
/// telemetry on success. Instances bound to Local report <c>hasLiveStatus:false</c> with no probe.
/// (docs/12-remote-orchestration.md §8/§10)
/// </summary>
public sealed record RefreshInstanceStatusCommand(Guid InstanceId) : ICommand<InstanceStatusDto>, IAuthorizedRequest
{
    public IReadOnlyCollection<string> RequiredPermissions => [WireGuardPermissions.Instances.Read];
}

public sealed record InstanceStatusDto(
    Guid InstanceId,
    bool HasLiveStatus,
    string State,
    int? ListenPort,
    DateTimeOffset ObservedAtUtc,
    bool HasDrift,
    string? DriftDetail,
    IReadOnlyList<PeerTelemetryDto> Peers);

public sealed record PeerTelemetryDto(
    string PublicKey,
    DateTimeOffset? LastHandshakeAtUtc,
    long RxBytes,
    long TxBytes,
    string? Endpoint);

public sealed class RefreshInstanceStatusCommandHandler(IInstanceStatusSync statusSync)
    : ICommandHandler<RefreshInstanceStatusCommand, InstanceStatusDto>
{
    public async Task<Result<InstanceStatusDto>> Handle(RefreshInstanceStatusCommand command, CancellationToken cancellationToken)
    {
        var result = await statusSync.SyncAsync(command.InstanceId, cancellationToken);
        if (result.IsFailure)
        {
            return result.Error;
        }

        var status = result.Value;
        var peers = status.Peers
            .Select(p => new PeerTelemetryDto(p.PublicKey, p.LastHandshakeAt, p.RxBytes, p.TxBytes, p.Endpoint))
            .ToList();

        return new InstanceStatusDto(
            command.InstanceId, status.HasLiveStatus, status.State.ToString(), status.ListenPort, status.ObservedAtUtc,
            status.HasDrift, status.DriftDetail, peers);
    }
}
