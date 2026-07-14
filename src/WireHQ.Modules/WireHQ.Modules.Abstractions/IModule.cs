using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace WireHQ.Modules.Abstractions;

/// <summary>
/// The contract every feature module implements. The host discovers all <see cref="IModule"/>s,
/// asks each whether it <see cref="IsEnabled"/> (edition/license/per-tenant flags), and only then
/// registers its services and routes — so adding a module is dropping in an assembly, never
/// editing the host. The same mechanism gates Enterprise-only modules and a future third-party
/// plugin ecosystem. (docs/02-architecture.md)
/// </summary>
public interface IModule
{
    /// <summary>Stable module name, also used for entitlement/feature-flag keys (e.g. "wireguard").</summary>
    string Name { get; }

    string Version { get; }

    /// <summary>Whether this module should load in the current deployment (edition/license/config).</summary>
    bool IsEnabled(IConfiguration configuration);

    /// <summary>Register the module's services into the host container.</summary>
    void RegisterServices(IServiceCollection services, IConfiguration configuration);

    /// <summary>Map the module's HTTP endpoints. Convention: route under <c>/api/v{n}/{name}</c>.</summary>
    void MapEndpoints(IEndpointRouteBuilder endpoints);
}
