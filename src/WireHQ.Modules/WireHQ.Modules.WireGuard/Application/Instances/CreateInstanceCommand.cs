using FluentValidation;
using Microsoft.EntityFrameworkCore;
using WireHQ.Application.Abstractions;
using WireHQ.Application.Abstractions.Persistence;
using WireHQ.Application.Common.Messaging;
using WireHQ.Application.Entitlements;
using WireHQ.Domain.Auditing;
using WireHQ.Modules.WireGuard.Authorization;
using WireHQ.Modules.WireGuard.Domain;
using WireHQ.Modules.WireGuard.Providers;
using WireHQ.Modules.WireGuard.Services;
using WireHQ.Shared.Results;

namespace WireHQ.Modules.WireGuard.Application.Instances;

/// <summary>Creates a WireGuard instance: generates the server keypair (encrypted) and provisions it via the provider.</summary>
public sealed record CreateInstanceCommand(
    Guid NetworkId,
    string Name,
    int ListenPort,
    string InterfaceAddress,
    string? EndpointHost,
    IReadOnlyList<string>? Dns,
    int? Mtu,
    string? Slug) : ICommand<CreateInstanceResponse>, IAuthorizedRequest, IRequiresVerifiedEmail
{
    public IReadOnlyCollection<string> RequiredPermissions => [WireGuardPermissions.Instances.Manage];
}

public sealed record CreateInstanceResponse(Guid Id, string Slug, string PublicKey, int ListenPort);

public sealed class CreateInstanceCommandValidator : AbstractValidator<CreateInstanceCommand>
{
    public CreateInstanceCommandValidator()
    {
        RuleFor(x => x.NetworkId).NotEmpty();
        RuleFor(x => x.Name).NotEmpty().MaximumLength(WireGuardInstance.MaxNameLength);
        RuleFor(x => x.ListenPort).InclusiveBetween(1, 65535);
        RuleFor(x => x.InterfaceAddress).NotEmpty()
            .Matches(@"^\d{1,3}(\.\d{1,3}){3}/\d{1,2}$").WithMessage("Interface address must look like 10.8.0.1/24.");
        RuleFor(x => x.Dns).ValidDnsServers();
    }
}

public sealed class CreateInstanceCommandHandler(
    IApplicationDbContext dbContext,
    ITenantContext tenant,
    IKeyManagementService keys,
    IWireGuardProviderFactory providerFactory,
    IConfigurationService configuration,
    IConfigVersionWriter configVersions,
    IEntitlementService entitlements,
    IAuditWriter audit)
    : ICommandHandler<CreateInstanceCommand, CreateInstanceResponse>
{
    public async Task<Result<CreateInstanceResponse>> Handle(CreateInstanceCommand command, CancellationToken cancellationToken)
    {
        if (tenant.OrganizationId is not { } organizationId)
        {
            return Error.Forbidden("auth.tenant_required", "No active organization.");
        }

        // Plan quota: the org's plan caps how many instances (gateways) it can manage.
        var instanceCount = await dbContext.Set<WireGuardInstance>().CountAsync(cancellationToken);
        var withinQuota = await entitlements.EnsureCanAddAsync(PlanResource.Instances, instanceCount, cancellationToken);
        if (withinQuota.IsFailure)
        {
            return withinQuota.Error;
        }

        var networkExists = await dbContext.Set<WireGuardNetwork>()
            .AnyAsync(n => n.Id == command.NetworkId, cancellationToken);
        if (!networkExists)
        {
            return WireGuardErrors.Network.NotFound;
        }

        // Phase 1 ships the config-only Local provider; remote providers add a ProviderType field later.
        const WireGuardProviderType providerType = WireGuardProviderType.Local;

        // Pre-generate the id so the server key can be linked to the instance before the save.
        var instanceId = Guid.CreateVersion7();
        var serverKey = keys.GenerateAndStoreKeyPair(organizationId, KeyOwnerType.Instance, instanceId);

        var result = WireGuardInstance.Create(
            instanceId, organizationId, command.NetworkId, command.Name, command.Slug,
            providerType, command.ListenPort, command.InterfaceAddress, serverKey.PublicKey, serverKey.KeyMaterialId);
        if (result.IsFailure)
        {
            return result.Error;
        }

        var instance = result.Value;
        if (command.Dns is { Count: > 0 })
        {
            instance.SetDns(command.Dns);
        }

        instance.SetEndpoint(command.EndpointHost);
        if (command.Mtu is { } mtu)
        {
            instance.SetMtu(mtu);
        }

        dbContext.Set<WireGuardInstance>().Add(instance);

        // Route through the provider (config-only succeeds with no external id).
        var provider = providerFactory.Resolve(providerType);
        var spec = new ProvisionInstance(
            instance.Id, instance.Name, instance.ListenPort, instance.InterfaceAddress, serverKey.PrivateKey,
            instance.Dns.ToArray(), instance.Mtu, instance.EndpointHost, new Dictionary<string, string>());

        var provisionResult = await provider.CreateInstanceAsync(spec, cancellationToken);
        if (provisionResult.IsFailure)
        {
            return provisionResult.Error;
        }

        instance.SetExternalId(provisionResult.Value.ExternalId);

        // Version the server (interface) config. No peers exist yet at creation, so this is the
        // [Interface] definition; per-peer blocks are versioned per peer (config_versions Peer target).
        var serverConfig = configuration.RenderInstanceConfig(new InstanceConfigInput(
            serverKey.PrivateKey, instance.InterfaceAddress, instance.ListenPort, instance.Mtu, []));
        await configVersions.WriteAsync(ConfigTargetType.Instance, instance.Id, serverConfig, "created", cancellationToken);

        audit.Record("wg.instance.created", AuditOutcome.Success, nameof(WireGuardInstance), instance.Id.ToString(),
            new { instance.Name, instance.ListenPort, ProviderType = providerType.ToString() });

        return new CreateInstanceResponse(instance.Id, instance.Slug.Value, instance.PublicKey, instance.ListenPort);
    }
}
