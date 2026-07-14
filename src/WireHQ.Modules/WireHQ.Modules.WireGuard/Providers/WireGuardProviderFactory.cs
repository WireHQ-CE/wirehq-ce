namespace WireHQ.Modules.WireGuard.Providers;

/// <summary>
/// Resolves the registered <see cref="IWireGuardProvider"/> for a given type. New providers
/// (kernel, pfSense, …) register themselves in DI and become resolvable with no change here or in
/// the services. Falls back to the Local (config-only) provider when a type isn't registered.
/// </summary>
public sealed class WireGuardProviderFactory(IEnumerable<IWireGuardProvider> providers) : IWireGuardProviderFactory
{
    private readonly IReadOnlyDictionary<WireGuardProviderType, IWireGuardProvider> _byType =
        providers.ToDictionary(p => p.Type);

    public IWireGuardProvider Resolve(WireGuardProviderType type) =>
        _byType.TryGetValue(type, out var provider)
            ? provider
            : _byType[WireGuardProviderType.Local];
}
