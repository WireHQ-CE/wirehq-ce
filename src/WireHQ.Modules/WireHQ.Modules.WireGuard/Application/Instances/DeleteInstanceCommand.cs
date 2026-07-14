using Microsoft.EntityFrameworkCore;
using WireHQ.Application.Abstractions;
using WireHQ.Application.Abstractions.Persistence;
using WireHQ.Application.Common.Messaging;
using WireHQ.Domain.Auditing;
using WireHQ.Modules.WireGuard.Authorization;
using WireHQ.Modules.WireGuard.Domain;
using WireHQ.Modules.WireGuard.Providers;
using WireHQ.Shared.Results;

namespace WireHQ.Modules.WireGuard.Application.Instances;

/// <summary>Deletes an instance: soft-deletes it and its peers, revokes all related keys, and tears down via the provider.</summary>
public sealed record DeleteInstanceCommand(Guid Id) : ICommand, IAuthorizedRequest
{
    public IReadOnlyCollection<string> RequiredPermissions => [WireGuardPermissions.Instances.Manage];
}

public sealed class DeleteInstanceCommandHandler(
    IApplicationDbContext dbContext,
    IWireGuardProviderFactory providerFactory,
    IDateTimeProvider clock,
    IAuditWriter audit)
    : ICommandHandler<DeleteInstanceCommand>
{
    public async Task<Result> Handle(DeleteInstanceCommand command, CancellationToken cancellationToken)
    {
        var instance = await dbContext.Set<WireGuardInstance>()
            .FirstOrDefaultAsync(i => i.Id == command.Id, cancellationToken);

        if (instance is null)
        {
            return WireGuardErrors.Instance.NotFound;
        }

        var peers = await dbContext.Set<Peer>()
            .Where(p => p.InstanceId == instance.Id)
            .ToListAsync(cancellationToken);
        var peerIds = peers.Select(p => p.Id).ToList();

        // Revoke all key material owned by the instance or its peers.
        var keyMaterials = await dbContext.Set<KeyMaterial>()
            .Where(k => (k.OwnerType == KeyOwnerType.Instance && k.OwnerId == instance.Id)
                        || (k.OwnerType == KeyOwnerType.Peer && peerIds.Contains(k.OwnerId)))
            .ToListAsync(cancellationToken);
        foreach (var key in keyMaterials)
        {
            key.Revoke(clock.UtcNow);
        }

        // Soft-delete peers, tear down the instance on the provider, soft-delete the instance.
        dbContext.Set<Peer>().RemoveRange(peers);

        var provider = providerFactory.Resolve(instance.ProviderType);
        var providerRef = new ProviderInstanceRef(instance.Id, instance.ExternalId,
            instance.ProviderSettings.ToDictionary(kv => kv.Key, kv => kv.Value));
        await provider.DeleteInstanceAsync(providerRef, cancellationToken);

        instance.MarkDeleted();
        dbContext.Set<WireGuardInstance>().Remove(instance);

        audit.Record("wg.instance.deleted", AuditOutcome.Success, nameof(WireGuardInstance), instance.Id.ToString(),
            new { peerCount = peers.Count });
        return Result.Success();
    }
}
