using FluentValidation;
using WireHQ.Application.Abstractions;
using WireHQ.Application.Abstractions.Persistence;
using WireHQ.Application.Common.Messaging;
using WireHQ.Modules.WireGuard.Authorization;
using WireHQ.Modules.WireGuard.Domain;
using WireHQ.Shared.Results;

namespace WireHQ.Modules.WireGuard.Application.Networks;

/// <summary>Creates an address pool (CIDR) that peers are allocated from.</summary>
public sealed record CreateNetworkCommand(string Name, string Cidr, IReadOnlyList<string>? Dns)
    : ICommand<CreateNetworkResponse>, IAuthorizedRequest, IAuditableRequest
{
    public IReadOnlyCollection<string> RequiredPermissions => [WireGuardPermissions.Instances.Manage];

    // Audited declaratively: the AuditBehavior records this action + the change diff for the new network.
    public string AuditAction => "wg.network.created";
}

public sealed record CreateNetworkResponse(Guid Id, string Name, string Cidr);

public sealed class CreateNetworkCommandValidator : AbstractValidator<CreateNetworkCommand>
{
    public CreateNetworkCommandValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(WireGuardNetwork.MaxNameLength);
        RuleFor(x => x.Cidr).NotEmpty().Matches(@"^\d{1,3}(\.\d{1,3}){3}/\d{1,2}$")
            .WithMessage("CIDR must look like 10.8.0.0/24.");
        RuleFor(x => x.Dns).ValidDnsServers();
    }
}

public sealed class CreateNetworkCommandHandler(
    IApplicationDbContext dbContext,
    ITenantContext tenant)
    : ICommandHandler<CreateNetworkCommand, CreateNetworkResponse>
{
    public async Task<Result<CreateNetworkResponse>> Handle(CreateNetworkCommand command, CancellationToken cancellationToken)
    {
        if (tenant.OrganizationId is not { } organizationId)
        {
            return Error.Forbidden("auth.tenant_required", "No active organization.");
        }

        var result = WireGuardNetwork.Create(organizationId, command.Name, command.Cidr);
        if (result.IsFailure)
        {
            return result.Error;
        }

        var network = result.Value;
        if (command.Dns is { Count: > 0 })
        {
            network.SetDns(command.Dns);
        }

        dbContext.Set<WireGuardNetwork>().Add(network);

        await Task.CompletedTask;
        return new CreateNetworkResponse(network.Id, network.Name, network.Cidr.ToString());
    }
}
