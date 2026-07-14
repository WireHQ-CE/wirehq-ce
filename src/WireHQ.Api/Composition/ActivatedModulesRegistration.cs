using WireHQ.Api.Modules;
using WireHQ.Api.Updates;
using WireHQ.Application.Entitlements;
using WireHQ.Application.Features.Modules;
using WireHQ.Application.Updates;
using WireHQ.Infrastructure.Modules;
using WireHQ.Infrastructure.Persistence.Seeding;

namespace WireHQ.Api.Composition;

/// <summary>
/// The <b>Community Edition</b> build of the module-activation composition seam
/// (docs/29-ce-marketplace-modules.md M-17, ADR-046). This file OVERLAYS the kept-core no-op of the same name,
/// so it exists only in the generated CE. It wires the CE-only activation runtime — the sanctioned way to
/// <i>add</i> CE-only backend wiring without editing the shared DI files (which the generator only patches by
/// line-deletion), mirroring the <c>AddLicensing</c> seam.
///
/// <para>The activation store, its controller, and the command/query handlers are auto-discovered (EF
/// configuration scanning, MediatR, and controller discovery), so the explicit registrations here are: the
/// activation-store-backed reader (over the kept-core no-op); the licensing call-home client (a typed
/// <c>HttpClient</c>); and the weekly verify loop. These run last in the Program.cs composition chain, so the
/// reader swap wins by last-registration over <see cref="NoActivatedModules"/>.</para>
/// </summary>
internal static class ActivatedModulesRegistration
{
    public static IServiceCollection AddActivatedModules(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<IActivatedModuleReader, ActivatedModuleReader>();

        // The call-home client for the hosted licensing service (docs/29 M-7). The destination is
        // operator-configured (Modules:LicensingBaseUrl), not tenant-supplied, so no SSRF guard is needed; a
        // short timeout keeps a slow service from stalling an activation request or a verify pass.
        services.AddHttpClient<ILicensingClient, HttpLicensingClient>(client => client.Timeout = TimeSpan.FromSeconds(15));

        // The weekly re-verify loop (refresh tokens + grace, apply revocation; nag-don't-kill on outage).
        services.AddHostedService<ModuleVerifyHostedService>();

        // The one-shot, opt-in re-edition of existing default-Enterprise orgs → CommunityEdition (docs/29 M-15).
        // Registered ONLY here (the CE-only seam) — never in the shared Infrastructure DI — so it can never run in
        // a SaaS build; it is also flag-gated (Entitlements:ReeditionExistingOrgs, default off).
        services.AddScoped<IDataSeeder, CommunityEditionReeditionSeeder>();

        // CE update notifications (docs/30): the anonymous signed-manifest poller + the singleton status provider
        // that overrides the kept-core no-op (last-registration-wins). The manifest destination is
        // operator/WireHQ-configured (not tenant-supplied), so — like the licensing client — no SSRF guard.
        services.AddHttpClient<SignedManifestClient>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(15);
            // The signed manifest is a tiny token; cap the buffer so a hostile/poisoned host can't OOM the API
            // with a huge body (an over-limit response throws → the client's fail-soft catch treats it as no manifest).
            client.MaxResponseContentBufferSize = 64 * 1024;
        });
        services.AddSingleton<PolledUpdateStatusProvider>();
        services.AddSingleton<IUpdateStatusProvider>(sp => sp.GetRequiredService<PolledUpdateStatusProvider>());
        services.AddHostedService<UpdateCheckHostedService>();

        return services;
    }
}
