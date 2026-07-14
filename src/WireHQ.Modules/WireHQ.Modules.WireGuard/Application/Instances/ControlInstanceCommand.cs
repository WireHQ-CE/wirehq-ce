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

/// <summary>Starts/stops/restarts an instance via its provider (config-only providers report this unsupported).</summary>
public sealed record ControlInstanceCommand(Guid Id, string Action) : ICommand, IAuthorizedRequest
{
    public IReadOnlyCollection<string> RequiredPermissions => [WireGuardPermissions.Instances.Manage];
}

public sealed class ControlInstanceCommandHandler(
    IApplicationDbContext dbContext,
    IWireGuardProviderFactory providerFactory,
    IDateTimeProvider clock,
    IAuditWriter audit)
    : ICommandHandler<ControlInstanceCommand>
{
    public async Task<Result> Handle(ControlInstanceCommand command, CancellationToken cancellationToken)
    {
        InstanceAction? action = command.Action.Trim().ToLowerInvariant() switch
        {
            "start" => InstanceAction.Start,
            "stop" => InstanceAction.Stop,
            "restart" => InstanceAction.Restart,
            _ => null,
        };

        if (action is null)
        {
            return Error.Validation("wg.instance.invalid_action", "Action must be start, stop or restart.");
        }

        var instance = await dbContext.Set<WireGuardInstance>()
            .FirstOrDefaultAsync(i => i.Id == command.Id, cancellationToken);

        if (instance is null)
        {
            return WireGuardErrors.Instance.NotFound;
        }

        var provider = providerFactory.Resolve(instance.ProviderType);
        var providerRef = new ProviderInstanceRef(instance.Id, instance.ExternalId,
            instance.ProviderSettings.ToDictionary(kv => kv.Key, kv => kv.Value));

        var result = await provider.ControlInstanceAsync(providerRef, action.Value, cancellationToken);
        if (result.IsFailure)
        {
            return result.Error;
        }

        // Reflect the new desired state (providers that actually control the interface).
        instance.ChangeStatus(action.Value == InstanceAction.Stop ? InstanceStatus.Stopped : InstanceStatus.Running, clock.UtcNow);
        audit.Record($"wg.instance.{action.Value.ToString().ToLowerInvariant()}", AuditOutcome.Success,
            nameof(WireGuardInstance), instance.Id.ToString());
        return Result.Success();
    }
}
