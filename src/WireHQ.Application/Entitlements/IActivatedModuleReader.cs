namespace WireHQ.Application.Entitlements;

/// <summary>
/// Supplies the feature keys unlocked by the currently-valid activated Marketplace modules for the active
/// organisation/install. Kept-core (idle in SaaS) so the entitlement union in <see cref="EntitlementService"/> is
/// exercised by the main test suite; the SaaS build binds <see cref="NoActivatedModules"/> (SaaS unlocks
/// capability through plan bundles, not module activations), and the CE build binds an implementation backed by
/// the local activation store + licence verification. (docs/29-ce-marketplace-modules.md M-4/M-17)
/// </summary>
public interface IActivatedModuleReader
{
    /// <summary>
    /// The feature keys granted by valid (non-revoked, in-grace) activated modules for the active org. Empty in
    /// SaaS and on a CE install with no activated modules — the union then leaves the base plan untouched.
    /// </summary>
    Task<IReadOnlySet<string>> ActiveFeatureKeysAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The default reader: no activated modules. Bound in every edition except a CE install that has wired the real
/// activation store — so SaaS is a strict no-op (zero behaviour change) and CE degrades safely to "no modules"
/// until Wave 2 lands the store. (docs/29-ce-marketplace-modules.md M-17)
/// </summary>
public sealed class NoActivatedModules : IActivatedModuleReader
{
    private static readonly IReadOnlySet<string> None = new HashSet<string>();

    public Task<IReadOnlySet<string>> ActiveFeatureKeysAsync(CancellationToken cancellationToken) =>
        Task.FromResult(None);
}
