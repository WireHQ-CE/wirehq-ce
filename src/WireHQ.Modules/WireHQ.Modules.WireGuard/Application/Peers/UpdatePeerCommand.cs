using FluentValidation;
using Microsoft.EntityFrameworkCore;
using WireHQ.Application.Abstractions;
using WireHQ.Application.Abstractions.Persistence;
using WireHQ.Application.Common.Messaging;
using WireHQ.Domain.Auditing;
using WireHQ.Modules.WireGuard.Authorization;
using WireHQ.Modules.WireGuard.Domain;
using WireHQ.Modules.WireGuard.Providers;
using WireHQ.Modules.WireGuard.Services;
using WireHQ.Shared.Results;

namespace WireHQ.Modules.WireGuard.Application.Peers;

/// <summary>
/// Edits a peer's profile and routing. When a config-affecting field (allowed IPs / keepalive)
/// changes and the server holds the key, the peer config is re-rendered, re-applied via the provider,
/// and a new config version is recorded.
/// </summary>
public sealed record UpdatePeerCommand(
    Guid PeerId,
    string? Name,
    string? Department,
    string? DeviceType,
    IReadOnlyList<string>? AllowedIps,
    int? PersistentKeepalive) : ICommand, IAuthorizedRequest
{
    public IReadOnlyCollection<string> RequiredPermissions => [WireGuardPermissions.Peers.Manage];
}

public sealed class UpdatePeerCommandValidator : AbstractValidator<UpdatePeerCommand>
{
    public UpdatePeerCommandValidator()
    {
        RuleFor(x => x.PeerId).NotEmpty();
        RuleFor(x => x.Name).MaximumLength(Peer.MaxNameLength).When(x => x.Name is not null);
    }
}

public sealed class UpdatePeerCommandHandler(
    IApplicationDbContext dbContext,
    IPeerConfigApplier peerConfig,
    IAuditWriter audit)
    : ICommandHandler<UpdatePeerCommand>
{
    public async Task<Result> Handle(UpdatePeerCommand command, CancellationToken cancellationToken)
    {
        var peer = await dbContext.Set<Peer>().FirstOrDefaultAsync(p => p.Id == command.PeerId, cancellationToken);
        if (peer is null)
        {
            return WireGuardErrors.Peer.NotFound;
        }

        if (command.Name is not null)
        {
            var rename = peer.Rename(command.Name);
            if (rename.IsFailure)
            {
                return rename.Error;
            }
        }

        if (command.Department is not null || command.DeviceType is not null)
        {
            peer.SetProfile(command.Department ?? peer.Department, command.DeviceType ?? peer.DeviceType);
        }

        var configAffected = false;
        if (command.AllowedIps is not null)
        {
            peer.SetAllowedIps(command.AllowedIps);
            configAffected = true;
        }

        if (command.PersistentKeepalive is not null)
        {
            peer.SetKeepalive(command.PersistentKeepalive);
            configAffected = true;
        }

        // Re-render, re-apply, and version the config only when routing changed (the applier no-ops when the
        // server doesn't hold the peer key). Shared with the Access Policies apply path (docs/22).
        if (configAffected)
        {
            var applied = await peerConfig.ApplyAsync(peer, "updated", cancellationToken);
            if (applied.IsFailure)
            {
                return applied.Error;
            }
        }

        audit.Record("wg.peer.updated", AuditOutcome.Success, nameof(Peer), peer.Id.ToString());
        return Result.Success();
    }
}
