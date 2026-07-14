using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using WireHQ.Application.Abstractions.Licensing;
using WireHQ.Application.Abstractions.Security;

namespace WireHQ.Licensing;

public static class DependencyInjection
{
    /// <summary>
    /// Registers licence-token signing and verification from the <c>Licensing</c> configuration section.
    /// The key ring is built once at first resolve (it owns the unmanaged signing key); it depends on
    /// <see cref="ISecretProtector"/> to decrypt the private key, so register the security adapters first.
    ///
    /// Wire this from the hosted (SaaS) composition only — a self-hosted install will register a
    /// verify-only variant in a later wave (docs/19-marketplace-licensing.md, ADR-036). Until the first
    /// consumer (licence issuance, wave 2) resolves these, nothing here runs.
    /// </summary>
    public static IServiceCollection AddLicensing(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<LicensingKeyOptions>(configuration.GetSection(LicensingKeyOptions.SectionName));

        services.AddSingleton(sp => LicensingKeyRing.Create(
            sp.GetRequiredService<IOptions<LicensingKeyOptions>>().Value,
            sp.GetRequiredService<ISecretProtector>()));

        services.AddSingleton<ILicenceTokenSigner, LicenceTokenSigner>();
        services.AddSingleton<ILicenceTokenVerifier, LicenceTokenVerifier>();

        return services;
    }
}
