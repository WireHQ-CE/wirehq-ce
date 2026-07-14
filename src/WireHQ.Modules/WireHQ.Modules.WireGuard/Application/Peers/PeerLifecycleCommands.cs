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

public sealed record EnablePeerCommand(Guid PeerId) : ICommand, IAuthorizedRequest
{
    public IReadOnlyCollection<string> RequiredPermissions => [WireGuardPermissions.Peers.Manage];
}

public sealed record DisablePeerCommand(Guid PeerId) : ICommand, IAuthorizedRequest
{
    public IReadOnlyCollection<string> RequiredPermissions => [WireGuardPermissions.Peers.Manage];
}

public sealed record DeletePeerCommand(Guid PeerId) : ICommand, IAuthorizedRequest
{
    public IReadOnlyCollection<string> RequiredPermissions => [WireGuardPermissions.Peers.Manage];
}

/// <summary>Shared helpers for resolving a peer + its instance and pushing peer state to the provider.</summary>
internal static class PeerProviderHelper
{
    public static async Task<(Peer? Peer, WireGuardInstance? Instance)> LoadAsync(
        IApplicationDbContext db, Guid peerId, CancellationToken ct)
    {
        var peer = await db.Set<Peer>().FirstOrDefaultAsync(p => p.Id == peerId, ct);
        if (peer is null)
        {
            return (null, null);
        }

        var instance = await db.Set<WireGuardInstance>().FirstOrDefaultAsync(i => i.Id == peer.InstanceId, ct);
        return (peer, instance);
    }

    public static ProviderInstanceRef Ref(WireGuardInstance instance) =>
        new(instance.Id, instance.ExternalId, instance.ProviderSettings.ToDictionary(kv => kv.Key, kv => kv.Value));
}

public sealed class EnablePeerCommandHandler(
    IApplicationDbContext dbContext, IWireGuardProviderFactory providerFactory, IKeyManagementService keys, IAuditWriter audit)
    : ICommandHandler<EnablePeerCommand>
{
    public async Task<Result> Handle(EnablePeerCommand command, CancellationToken cancellationToken)
    {
        var (peer, instance) = await PeerProviderHelper.LoadAsync(dbContext, command.PeerId, cancellationToken);
        if (peer is null || instance is null)
        {
            return WireGuardErrors.Peer.NotFound;
        }

        var enable = peer.Enable();
        if (enable.IsFailure)
        {
            return enable.Error;
        }

        var psk = peer.PresharedKeyId is { } id ? await keys.RevealAsync(id, cancellationToken) : null;
        var provider = providerFactory.Resolve(instance.ProviderType);
        await provider.ApplyPeerAsync(PeerProviderHelper.Ref(instance),
            new ProviderPeerSpec(peer.PublicKey, psk, peer.AllowedIps.ToList(), null, peer.PersistentKeepalive), cancellationToken);

        audit.Record("wg.peer.enabled", AuditOutcome.Success, nameof(Peer), peer.Id.ToString());
        return Result.Success();
    }
}

public sealed class DisablePeerCommandHandler(
    IApplicationDbContext dbContext, IWireGuardProviderFactory providerFactory, IAuditWriter audit)
    : ICommandHandler<DisablePeerCommand>
{
    public async Task<Result> Handle(DisablePeerCommand command, CancellationToken cancellationToken)
    {
        var (peer, instance) = await PeerProviderHelper.LoadAsync(dbContext, command.PeerId, cancellationToken);
        if (peer is null || instance is null)
        {
            return WireGuardErrors.Peer.NotFound;
        }

        var disable = peer.Disable();
        if (disable.IsFailure)
        {
            return disable.Error;
        }

        var provider = providerFactory.Resolve(instance.ProviderType);
        await provider.RemovePeerAsync(PeerProviderHelper.Ref(instance), peer.PublicKey, cancellationToken);

        audit.Record("wg.peer.disabled", AuditOutcome.Success, nameof(Peer), peer.Id.ToString());
        return Result.Success();
    }
}

public sealed class DeletePeerCommandHandler(
    IApplicationDbContext dbContext, IWireGuardProviderFactory providerFactory, IKeyManagementService keys, IDateTimeProvider clock, IAuditWriter audit)
    : ICommandHandler<DeletePeerCommand>
{
    public async Task<Result> Handle(DeletePeerCommand command, CancellationToken cancellationToken)
    {
        var (peer, instance) = await PeerProviderHelper.LoadAsync(dbContext, command.PeerId, cancellationToken);
        if (peer is null || instance is null)
        {
            return WireGuardErrors.Peer.NotFound;
        }

        peer.Revoke();
        await keys.RevokeForOwnerAsync(KeyOwnerType.Peer, peer.Id, clock.UtcNow, cancellationToken);

        var provider = providerFactory.Resolve(instance.ProviderType);
        await provider.RemovePeerAsync(PeerProviderHelper.Ref(instance), peer.PublicKey, cancellationToken);

        dbContext.Set<Peer>().Remove(peer);
        audit.Record("wg.peer.deleted", AuditOutcome.Success, nameof(Peer), peer.Id.ToString());
        return Result.Success();
    }
}
