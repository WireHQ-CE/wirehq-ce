using FluentValidation;
using Microsoft.EntityFrameworkCore;
using WireHQ.Application.Abstractions.Persistence;
using WireHQ.Application.Common.Messaging;
using WireHQ.Modules.WireGuard.Authorization;
using WireHQ.Modules.WireGuard.Domain;
using WireHQ.Shared.Results;

namespace WireHQ.Modules.WireGuard.Application.Networks;

/// <summary>Updates a network's editable fields. CIDR is immutable — changing it post-allocation would orphan peer IPs.</summary>
public sealed record UpdateNetworkCommand(
    Guid Id,
    string? Name,
    IReadOnlyList<string>? Dns,
    IReadOnlyList<string>? DefaultAllowedIps) : ICommand, IAuthorizedRequest, IAuditableRequest
{
    public IReadOnlyCollection<string> RequiredPermissions => [WireGuardPermissions.Instances.Manage];

    // Audited declaratively: the AuditBehavior records this action + a before/after diff of the changed fields.
    public string AuditAction => "wg.network.updated";
}

public sealed class UpdateNetworkCommandValidator : AbstractValidator<UpdateNetworkCommand>
{
    public UpdateNetworkCommandValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
        RuleFor(x => x.Name).MaximumLength(WireGuardNetwork.MaxNameLength).When(x => x.Name is not null);
        RuleFor(x => x.Dns).ValidDnsServers();
    }
}

public sealed class UpdateNetworkCommandHandler(IApplicationDbContext dbContext)
    : ICommandHandler<UpdateNetworkCommand>
{
    public async Task<Result> Handle(UpdateNetworkCommand command, CancellationToken cancellationToken)
    {
        var network = await dbContext.Set<WireGuardNetwork>().FirstOrDefaultAsync(n => n.Id == command.Id, cancellationToken);
        if (network is null)
        {
            return WireGuardErrors.Network.NotFound;
        }

        if (command.Name is not null)
        {
            var rename = network.Rename(command.Name);
            if (rename.IsFailure)
            {
                return rename.Error;
            }
        }

        if (command.Dns is not null)
        {
            network.SetDns(command.Dns);
        }

        if (command.DefaultAllowedIps is not null)
        {
            network.SetDefaultAllowedIps(command.DefaultAllowedIps);
        }

        return Result.Success();
    }
}
