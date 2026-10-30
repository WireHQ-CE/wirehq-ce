using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using WireHQ.Application.Abstractions;
using WireHQ.Application.Abstractions.Persistence;
using WireHQ.Infrastructure.Auditing;
using WireHQ.Infrastructure.Authorization;
using WireHQ.Infrastructure.Messaging;
using WireHQ.Infrastructure.Persistence;
using WireHQ.Infrastructure.Persistence.Interceptors;
using WireHQ.Infrastructure.Persistence.Seeding;
using WireHQ.Infrastructure.Time;

namespace WireHQ.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Default")
            ?? throw new InvalidOperationException("Connection string 'Default' is not configured.");

        // The edition new orgs are provisioned with (default Community; test/dev may override). (docs/commercial.md)
        var defaultEdition = Enum.TryParse<WireHQ.Domain.Organizations.OrganizationEdition>(
            configuration["Entitlements:DefaultEdition"], ignoreCase: true, out var edition)
            ? edition
            : WireHQ.Domain.Organizations.OrganizationEdition.Community;
        services.AddSingleton(new WireHQ.Application.Entitlements.EntitlementOptions { DefaultEdition = defaultEdition });

        // Whether self-serve signup is open (default true — the SaaS posture). Self-hosted installs set
        // Auth:OpenRegistration=false to run invite-only (docs/17-community-edition.md).
        var openRegistration = !bool.TryParse(configuration["Auth:OpenRegistration"], out var open) || open;
        services.AddSingleton(new WireHQ.Application.Features.Authentication.RegistrationOptions { OpenRegistration = openRegistration });

        // Whether the browser first-run setup is available (default false — the SaaS posture). Self-hosted
        // installs set Setup:Enabled=true; the endpoint self-disables once any user exists (docs/17).
        var setupEnabled = bool.TryParse(configuration["Setup:Enabled"], out var setupOn) && setupOn;
        services.AddSingleton(new WireHQ.Application.Features.Authentication.SetupOptions { Enabled = setupEnabled });

        // Operator-configured daily per-org SMS cap (docs/36 §7). Absent/0/negative ⇒ the default; the scheduler clamps
        // it to a finite floor + hard max (NEVER uncapped) via NotificationSendPolicy. Read here so the Application layer
        // never touches IConfiguration (same pattern as EntitlementOptions / RegistrationOptions).
        var smsDailyCap = int.TryParse(configuration["Notifications:Sms:DailyCapPerOrg"], out var cap) ? cap : 0;
        // Dark-boundary master switch (docs/36 §2/§10): real SMS sends stay OFF until Slice D flips it. Default FALSE —
        // any value other than an explicit "true" leaves the channel provably dark in code.
        var smsSendEnabled = bool.TryParse(configuration["Notifications:Sms:Enabled"], out var enabled) && enabled;
        // The pinned public base URL the inbound STOP/HELP webhook signature is verified against (docs/36 §5, Slice D) —
        // never Request.Host. Empty ⇒ inbound verification can never succeed (fail-closed), correct until the operator
        // stands the endpoint up.
        // Normalised, and deliberately with NO fallback to App:BaseUrl. The blank default is a documented fail-closed
        // kill switch on an anonymous endpoint; falling back would arm that endpoint on every install at upgrade time
        // with no operator action. A present-but-invalid value resolves to null for the same reason — silently using a
        // different origin would produce a signature that never matches, which looks identical to "no STOPs arriving".
        var smsInboundBaseUrl = WireHQ.Application.Features.Notifications.SmsInboundEndpoint.Normalize(
            configuration["Notifications:Sms:InboundWebhookBaseUrl"]);
        services.AddSingleton(new WireHQ.Application.Features.Notifications.SmsNotificationOptions
        {
            DailyCapPerOrg = smsDailyCap,
            Enabled = smsSendEnabled,
            InboundWebhookBaseUrl = smsInboundBaseUrl,
        });

        services.AddScoped<AuditableEntityInterceptor>();
        services.AddScoped<AuditChainInterceptor>();
        services.AddScoped<PublishDomainEventsInterceptor>();
        services.AddScoped<TenantConnectionInterceptor>();
        services.AddScoped<WebhookOutboxInterceptor>();
        services.AddScoped<NotificationOutboxInterceptor>();

        services.AddDbContext<ApplicationDbContext>((serviceProvider, options) =>
        {
            options.UseNpgsql(connectionString, npgsql =>
            {
                npgsql.MigrationsAssembly(typeof(ApplicationDbContext).Assembly.FullName);
                npgsql.MigrationsHistoryTable("__ef_migrations_history", "core");
            });

            options.UseSnakeCaseNamingConvention();

            options.AddInterceptors(
                serviceProvider.GetRequiredService<AuditableEntityInterceptor>(),
                // Links new audit rows into the per-tenant tamper-evident hash chain, after the auditable
                // stamping above has run. (ADR-031)
                serviceProvider.GetRequiredService<AuditChainInterceptor>(),
                // Captures webhook deliveries from new audit rows into the same transaction — the reliable outbox
                // (docs/26 §8). After the chain interceptor so it only reads (never re-hashes) the audit entries.
                serviceProvider.GetRequiredService<WebhookOutboxInterceptor>(),
                // Captures notification jobs from new audit rows into the same transaction — the notification
                // dispatch spine's outbox (docs/35 §4.1). Like the webhook outbox, after the chain interceptor.
                serviceProvider.GetRequiredService<NotificationOutboxInterceptor>(),
                serviceProvider.GetRequiredService<PublishDomainEventsInterceptor>(),
                // Sets the RLS GUCs (app.current_org / app.bypass_rls) on every connection open. (ADR-027)
                serviceProvider.GetRequiredService<TenantConnectionInterceptor>());
        });

        // Expose the context through its port so Application never sees the concrete type.
        services.AddScoped<IApplicationDbContext>(sp => sp.GetRequiredService<ApplicationDbContext>());

        services.AddSingleton<IDateTimeProvider, DateTimeProvider>();
        // Webhooks (kept-core): the subscription cache the outbox interceptor reads + the outbox dispatch scheduler.
        services.AddSingleton<WireHQ.Application.Features.Webhooks.WebhookSubscriptionCache>();
        services.AddSingleton<WireHQ.Application.Features.Webhooks.WebhookDispatchScheduler>();
        // Notifications (kept-core, docs/35): the route cache the outbox interceptor reads, the dispatch scheduler
        // (expand + send), and the Wave-1 free-core Email channel adapter.
        services.AddSingleton<WireHQ.Application.Features.Notifications.NotificationRouteCache>();
        services.AddSingleton<WireHQ.Application.Features.Notifications.NotificationDispatchScheduler>();
        services.AddScoped<WireHQ.Application.Abstractions.Notifications.INotificationChannel, Messaging.Notifications.EmailChannel>();
        services.AddScoped<WireHQ.Application.Abstractions.Notifications.INotificationChannel, Messaging.Notifications.ChatChannel>();
        // SMS channel (docs/36 Wave 4, kept-core) + its Twilio provider adapter behind the ISmsProvider seam. Ships in
        // every edition; a licence lights up the notifications.sms gate. Dark in Slice A (the phone resolver is empty).
        services.AddScoped<WireHQ.Application.Abstractions.Notifications.INotificationChannel, Messaging.Notifications.Sms.SmsChannel>();
        services.AddScoped<WireHQ.Application.Abstractions.Notifications.ISmsProvider, Messaging.Notifications.Sms.TwilioSmsProvider>();
        // No-billing-plane fallback: only takes effect when the SaaS reader registration above is
        // stripped (the Community Edition) — TryAdd is a no-op while it is present (docs/17 §5).
        services.TryAddScoped<IBillingSummaryReader, NullBillingSummaryReader>();
        services.AddScoped<IAuditWriter, AuditWriter>();
        services.AddScoped<IAuditChangeCapture, EfAuditChangeCapture>();
        services.AddScoped<IAuditChainVerifier, AuditChainVerifier>();
        services.AddScoped<IAuditRetentionService, AuditRetentionSweeper>();
        services.AddScoped<IPermissionService, PermissionService>();
        services.AddScoped<IEmailSender, SmtpEmailSender>();
        services.AddSingleton<IClientUrlBuilder, ClientUrlBuilder>();

        // Boot-time seeders: concrete registrations (tests resolve them directly) + an IDataSeeder
        // forwarding each — the initializer iterates the seam in Order (see IDataSeeder). One line
        // pair per seeder keeps the Community Edition strip a pure line-removal (docs/17 §5).
        services.AddScoped<PermissionSeeder>();
        services.AddScoped<IDataSeeder>(sp => sp.GetRequiredService<PermissionSeeder>());
        services.AddScoped<SystemRolePermissionReconciler>();
        services.AddScoped<IDataSeeder>(sp => sp.GetRequiredService<SystemRolePermissionReconciler>());
        services.AddScoped<PlatformSettingsSeeder>();
        services.AddScoped<IDataSeeder>(sp => sp.GetRequiredService<PlatformSettingsSeeder>());
        services.AddScoped<SelfHostOwnerSeeder>();
        services.AddScoped<IDataSeeder>(sp => sp.GetRequiredService<SelfHostOwnerSeeder>());
        services.AddScoped<ApplicationDbContextInitializer>();

        return services;
    }
}
