using FluentValidation;
using Microsoft.EntityFrameworkCore;
using WireHQ.Application.Abstractions.Persistence;
using WireHQ.Application.Common.Messaging;
using WireHQ.Domain.Auditing;
using WireHQ.Application.Abstractions;
using WireHQ.Modules.WireGuard.Authorization;
using WireHQ.Modules.WireGuard.Domain;
using WireHQ.Modules.WireGuard.Providers;
using WireHQ.Modules.WireGuard.Services;
using WireHQ.Shared.Results;

namespace WireHQ.Modules.WireGuard.Application.Instances;

/// <summary>Updates an instance's editable fields and re-applies via the provider.</summary>
public sealed record UpdateInstanceCommand(
    Guid Id,
    string? Name,
    string? Description,
    string? EndpointHost,
    IReadOnlyList<string>? Dns,
    int? Mtu) : ICommand, IAuthorizedRequest
{
    public IReadOnlyCollection<string> RequiredPermissions => [WireGuardPermissions.Instances.Manage];
}

public sealed class UpdateInstanceCommandValidator : AbstractValidator<UpdateInstanceCommand>
{
    public UpdateInstanceCommandValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
        RuleFor(x => x.Name).MaximumLength(WireGuardInstance.MaxNameLength).When(x => x.Name is not null);
        RuleFor(x => x.Dns).ValidDnsServers();
    }
}

public sealed class UpdateInstanceCommandHandler(
    IApplicationDbContext dbContext,
    IKeyManagementService keys,
    IWireGuardProviderFactory providerFactory,
    IConfigurationService configuration,
    IConfigVersionWriter configVersions,
    IAuditWriter audit)
    : ICommandHandler<UpdateInstanceCommand>
{
    public async Task<Result> Handle(UpdateInstanceCommand command, CancellationToken cancellationToken)
    {
        var instance = await dbContext.Set<WireGuardInstance>()
            .FirstOrDefaultAsync(i => i.Id == command.Id, cancellationToken);

        if (instance is null)
        {
            return WireGuardErrors.Instance.NotFound;
        }

        if (command.Name is not null)
        {
            var rename = instance.Rename(command.Name);
            if (rename.IsFailure)
            {
                return rename.Error;
            }
        }

        if (command.Description is not null)
        {
            instance.Describe(command.Description);
        }

        if (command.EndpointHost is not null)
        {
            instance.SetEndpoint(command.EndpointHost);
        }

        if (command.Dns is not null)
        {
            instance.SetDns(command.Dns);
        }

        if (command.Mtu is { } mtu)
        {
            instance.SetMtu(mtu);
        }

        // Re-apply desired state via the provider (no-op for config-only). An AgentManaged instance holds no
        // WireHQ private key, so it renders key-less and the provider spec carries an empty key.
        var privateKey = instance.PrivateKeyId is { } pkId
            ? await keys.RevealAsync(pkId, cancellationToken)
            : null;
        var provider = providerFactory.Resolve(instance.ProviderType);
        var spec = new ProvisionInstance(
            instance.Id, instance.Name, instance.ListenPort, instance.InterfaceAddress, privateKey ?? string.Empty,
            instance.Dns.ToArray(), instance.Mtu, instance.EndpointHost, instance.ProviderSettings.ToDictionary(kv => kv.Key, kv => kv.Value));

        var providerRef = new ProviderInstanceRef(instance.Id, instance.ExternalId, instance.ProviderSettings.ToDictionary(kv => kv.Key, kv => kv.Value));
        var applyResult = await provider.UpdateInstanceAsync(providerRef, spec, cancellationToken);
        if (applyResult.IsFailure)
        {
            return applyResult.Error;
        }

        // Re-version the server (interface) config to capture the change.
        var serverConfig = configuration.RenderInstanceConfig(new InstanceConfigInput(
            privateKey, instance.InterfaceAddress, instance.ListenPort, instance.Mtu, []));
        await configVersions.WriteAsync(ConfigTargetType.Instance, instance.Id, serverConfig, "updated", cancellationToken);

        audit.Record("wg.instance.updated", AuditOutcome.Success, nameof(WireGuardInstance), instance.Id.ToString());
        return Result.Success();
    }
}
