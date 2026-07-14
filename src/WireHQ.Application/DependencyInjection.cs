using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using WireHQ.Application.Common.Behaviors;
using WireHQ.Application.Entitlements;
using WireHQ.Application.Features.Authentication;
using WireHQ.Application.Memberships;
using WireHQ.Application.Organizations;

namespace WireHQ.Application;

public static class DependencyInjection
{
    /// <summary>
    /// Registers MediatR, all validators, and the request pipeline. Behavior order is
    /// significant — it is the order requests flow through:
    /// Tracing → Metrics → Logging → TenantScope → Validation → Captcha → Authorization → VerifiedEmail → Entitlement → UnitOfWork → Audit → Handler.
    /// Tracing is outermost so the use-case span is the parent of every behavior's logs + the handler's DB
    /// work (docs/15 §6). Metrics sits just inside it, recording RED per use case over the same scope (docs/15 §7). TenantScope runs early so the RLS bypass is established before any handler/validator query.
    /// Audit is innermost (inside UnitOfWork) so it observes the handler's pending changes and appends its
    /// entry to the same transaction the UnitOfWork commits — atomic with the action (docs/15 §5, ADR-031).
    /// </summary>
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        var assembly = typeof(DependencyInjection).Assembly;

        services.AddMediatR(cfg =>
        {
            cfg.RegisterServicesFromAssembly(assembly);
            cfg.AddOpenBehavior(typeof(TracingBehavior<,>));
            cfg.AddOpenBehavior(typeof(MetricsBehavior<,>));
            cfg.AddOpenBehavior(typeof(RequestLoggingBehavior<,>));
            cfg.AddOpenBehavior(typeof(TenantScopeBehavior<,>));
            cfg.AddOpenBehavior(typeof(ValidationBehavior<,>));
            cfg.AddOpenBehavior(typeof(CaptchaBehavior<,>));
            cfg.AddOpenBehavior(typeof(AuthorizationBehavior<,>));
            cfg.AddOpenBehavior(typeof(VerifiedEmailBehavior<,>));
            cfg.AddOpenBehavior(typeof(EntitlementBehavior<,>));
            cfg.AddOpenBehavior(typeof(UnitOfWorkBehavior<,>));
            cfg.AddOpenBehavior(typeof(AuditBehavior<,>));
        });

        services.AddValidatorsFromAssembly(assembly, includeInternalTypes: true);

        // Application-layer domain services (orchestration that spans aggregates).
        services.AddScoped<OrganizationProvisioner>();
        services.AddScoped<AuthSessionService>();
        services.AddScoped<UserInvitationService>();

        // Plan entitlements: a code-defined catalog + a per-request service resolving the org's plan.
        services.AddSingleton<IPlanCatalog, PlanCatalog>();
        services.AddScoped<IEntitlementService, EntitlementService>();
        // Marketplace-module unlocks unioned into the effective plan (docs/29 M-4/M-17). Default = no modules
        // (a strict no-op in SaaS); a CE install rebinds the reader to the real activation-store impl via the
        // AddActivatedModules composition seam (Program.cs, overlaid CE-only).
        services.AddScoped<IActivatedModuleReader, NoActivatedModules>();
        // The single owner of the base ∪ active-module union, shared by EntitlementService (active tenant) and
        // the anonymous API-key auth handler (a specific key-owning org — docs/29 M-16).
        services.AddScoped<IEffectiveFeatureSet, EffectiveFeatureSet>();

        // CE update-notification status (docs/30 U-6). Default = up-to-date (a strict no-op in SaaS, which is
        // WireHQ-operated + auto-deployed); a CE install rebinds this to the signed-manifest-poller-backed provider
        // via the AddActivatedModules composition seam. SINGLETON — a background poller writes it.
        services.AddSingleton<Updates.IUpdateStatusProvider, Updates.NoUpdateStatus>();

        return services;
    }
}
