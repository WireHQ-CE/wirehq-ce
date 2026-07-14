using System.Reflection;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace WireHQ.Modules.Abstractions;

/// <summary>Holds the modules that were enabled at startup, for endpoint mapping and introspection.</summary>
public sealed class ModuleManifest
{
    private readonly List<IModule> _modules = [];

    public IReadOnlyList<IModule> Modules => _modules;

    internal void Add(IModule module) => _modules.Add(module);
}

public static class ModuleRegistration
{
    /// <summary>
    /// Discovers <see cref="IModule"/> implementations in the given assemblies, enables those whose
    /// <see cref="IModule.IsEnabled"/> returns true, registers their services, and records them for
    /// endpoint mapping. With no feature modules present this is a safe no-op — the seam exists from
    /// day one so modules can be added later without touching the host.
    /// </summary>
    public static IServiceCollection AddModules(
        this IServiceCollection services, IConfiguration configuration, params Assembly[] assemblies)
    {
        var manifest = new ModuleManifest();

        var moduleTypes = assemblies
            .SelectMany(a => a.GetTypes())
            .Where(t => typeof(IModule).IsAssignableFrom(t) && t is { IsAbstract: false, IsInterface: false });

        foreach (var type in moduleTypes)
        {
            if (Activator.CreateInstance(type) is not IModule module || !module.IsEnabled(configuration))
            {
                continue;
            }

            module.RegisterServices(services, configuration);
            manifest.Add(module);
        }

        services.AddSingleton(manifest);
        return services;
    }

    /// <summary>Maps the endpoints of every enabled module.</summary>
    public static IEndpointRouteBuilder MapModuleEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var manifest = endpoints.ServiceProvider.GetRequiredService<ModuleManifest>();
        foreach (var module in manifest.Modules)
        {
            module.MapEndpoints(endpoints);
        }

        return endpoints;
    }
}
