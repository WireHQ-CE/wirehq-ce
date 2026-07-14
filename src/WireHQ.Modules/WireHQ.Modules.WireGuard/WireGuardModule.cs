using FluentValidation;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WireHQ.Application.Abstractions.Persistence;
using WireHQ.Application.Authorization;
using WireHQ.Modules.Abstractions;
using WireHQ.Modules.WireGuard.Authorization;
using WireHQ.Modules.WireGuard.Endpoints;
using WireHQ.Modules.WireGuard.Persistence;
using WireHQ.Modules.WireGuard.Providers;
using WireHQ.Modules.WireGuard.Providers.Local;
using WireHQ.Modules.WireGuard.Services;

namespace WireHQ.Modules.WireGuard;

/// <summary>
/// The WireGuard management module. Discovered, gated, and wired by the host via the
/// <see cref="IModule"/> contract — no host changes required. Registers its permissions, model
/// configuration, providers, CQRS handlers/validators, and HTTP endpoints. (docs/11-wireguard-module.md)
/// </summary>
public sealed class WireGuardModule : IModule
{
    public string Name => "wireguard";

    public string Version => "1.0.0";

    public bool IsEnabled(IConfiguration configuration)
    {
        // Enabled by default; toggle with Modules:WireGuard:Enabled = false.
        var value = configuration["Modules:WireGuard:Enabled"];
        return string.IsNullOrWhiteSpace(value) || (bool.TryParse(value, out var enabled) && enabled);
    }

    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        // Contribute to the platform catalogs (permissions + EF model/schema).
        services.AddSingleton<IPermissionContributor, WireGuardPermissionContributor>();
        services.AddSingleton<IModelConfigurationContributor, WireGuardModelContributor>();

        // Integration layer: the config-only provider is the default; future providers register
        // themselves the same way and become resolvable by the factory with no further changes.
        services.AddSingleton<IWireGuardProvider, LocalConfigWireGuardProvider>();
        services.AddSingleton<IWireGuardProviderFactory, WireGuardProviderFactory>();

        // Service layers.
        services.AddScoped<IKeyManagementService, KeyManagementService>();
        services.AddScoped<IAddressAllocator, AddressAllocator>();
        // The shared "a peer's routing changed, deploy it" step (used by UpdatePeerCommand + the policy applier).
        services.AddScoped<IPeerConfigApplier, PeerConfigApplier>();
        // A module-neutral read of the org's topology + a neutral batch writer of peer routing. Core; their only
        // consumers today are the SaaS Access Policies compiler/apply (docs/22 §6) — idle in the CE.
        services.AddScoped<WireHQ.Application.Abstractions.Networking.INetworkTopologyReader, NetworkTopologyReader>();
        services.AddScoped<WireHQ.Application.Abstractions.Networking.IPeerRoutingWriter, PeerRoutingWriter>();
        services.AddScoped<IConfigVersionWriter, ConfigVersionWriter>();
        services.AddSingleton<IConfigurationService, ConfigurationService>();
        services.AddSingleton<IQrCodeService, QrCodeService>();
        services.AddSingleton<IEnrollmentService, EnrollmentService>();

        // The module's CQRS handlers + validators (the host's pipeline behaviors apply to them).
        var assembly = typeof(WireGuardModule).Assembly;
        services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(assembly));
        services.AddValidatorsFromAssembly(assembly, includeInternalTypes: true);
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints) => endpoints.MapWireGuardEndpoints();
}
