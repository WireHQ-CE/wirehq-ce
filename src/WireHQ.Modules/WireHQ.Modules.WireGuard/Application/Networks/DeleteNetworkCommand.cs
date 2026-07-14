using Microsoft.EntityFrameworkCore;
using WireHQ.Application.Abstractions.Persistence;
using WireHQ.Application.Common.Messaging;
using WireHQ.Modules.WireGuard.Authorization;
using WireHQ.Modules.WireGuard.Domain;
using WireHQ.Shared.Results;

namespace WireHQ.Modules.WireGuard.Application.Networks;

/// <summary>Soft-deletes a network. Refuses while any instance still references it.</summary>
public sealed record DeleteNetworkCommand(Guid Id) : ICommand, IAuthorizedRequest, IAuditableRequest
{
    public IReadOnlyCollection<string> RequiredPermissions => [WireGuardPermissions.Instances.Manage];

    // Audited declaratively: the AuditBehavior records this action + the deleted network's snapshot.
    public string AuditAction => "wg.network.deleted";
}

public sealed class DeleteNetworkCommandHandler(IApplicationDbContext dbContext)
    : ICommandHandler<DeleteNetworkCommand>
{
    public async Task<Result> Handle(DeleteNetworkCommand command, CancellationToken cancellationToken)
    {
        var network = await dbContext.Set<WireGuardNetwork>().FirstOrDefaultAsync(n => n.Id == command.Id, cancellationToken);
        if (network is null)
        {
            return WireGuardErrors.Network.NotFound;
        }

        var hasInstances = await dbContext.Set<WireGuardInstance>().AnyAsync(i => i.NetworkId == network.Id, cancellationToken);
        if (hasInstances)
        {
            return WireGuardErrors.Network.HasInstances;
        }

        // Remove() on an ISoftDeletable is converted to a soft-delete by the auditable interceptor.
        dbContext.Set<WireGuardNetwork>().Remove(network);

        return Result.Success();
    }
}
