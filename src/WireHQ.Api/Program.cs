using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Sinks.OpenTelemetry;
using WireHQ.Api.Composition;
using WireHQ.Api.Extensions;
using WireHQ.Api.Middleware;
using WireHQ.Api.Observability;
using WireHQ.Application;
using WireHQ.Application.Common.Observability;
using WireHQ.Application.Abstractions;
using WireHQ.Application.Abstractions.Security;
using WireHQ.Infrastructure;
using WireHQ.Licensing;
using WireHQ.Infrastructure.Persistence;
using WireHQ.Infrastructure.Security;
using WireHQ.Identity;
using WireHQ.Modules.Abstractions;
using WireHQ.Modules.Orchestration.Observability;
using WireHQ.Shared.Observability;

// Bootstrap logger: captures failures that occur before the host is built.
Log.Logger = new LoggerConfiguration().WriteTo.Console().CreateBootstrapLogger();

try
{
    // Offline update-manifest tooling (docs/30 §5, U-4). Handled BEFORE building the host so they run in a bare
    // CI / offline context with only their own inputs — no DB, no config, no DI. Both use a DEDICATED update key,
    // separate from the licensing key.

    // Generate the update-manifest signing key pair. Prints the PUBLIC key (bake into the CE image) and the
    // PLAINTEXT private seed (keep OFFLINE — a CI secret / password manager, never in a deployment).
    //   dotnet WireHQ.Api.dll --generate-update-key
    if (args.Contains("--generate-update-key"))
    {
        Console.WriteLine(UpdateKeyCeremony.GenerateConfigBlock(DateTimeOffset.UtcNow));
        return;
    }

    // Sign an update manifest for publishing. Reads the base64 private seed from the UPDATE_SIGNING_SEED env var
    // and the manifest JSON from stdin; writes the signed PASETO token to stdout. Run offline / in the release
    // pipeline AFTER the release images are confirmed pullable (docs/30 U-12).
    //   echo "$MANIFEST_JSON" | UPDATE_SIGNING_SEED=... dotnet WireHQ.Api.dll --sign-update-manifest
    if (args.Contains("--sign-update-manifest"))
    {
        var seed = Environment.GetEnvironmentVariable("UPDATE_SIGNING_SEED");
        if (string.IsNullOrWhiteSpace(seed))
        {
            await Console.Error.WriteLineAsync(
                "UPDATE_SIGNING_SEED (base64 Ed25519 private seed from --generate-update-key) is required.");
            Environment.ExitCode = 1;
            return;
        }

        var manifestJson = await Console.In.ReadToEndAsync();
        WireHQ.Application.Updates.UpdateManifest? manifest;
        try
        {
            manifest = System.Text.Json.JsonSerializer.Deserialize<WireHQ.Application.Updates.UpdateManifest>(
                manifestJson,
                new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        }
        catch (System.Text.Json.JsonException ex)
        {
            await Console.Error.WriteLineAsync($"The manifest JSON on stdin was unreadable: {ex.Message}");
            Environment.ExitCode = 1;
            return;
        }

        if (manifest is null || string.IsNullOrWhiteSpace(manifest.LatestVersion))
        {
            await Console.Error.WriteLineAsync("A manifest JSON with a non-empty latestVersion is required on stdin.");
            Environment.ExitCode = 1;
            return;
        }

        string token;
        try
        {
            token = UpdateManifestCodec.Sign(manifest, seed);
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"Signing failed (check UPDATE_SIGNING_SEED): {ex.Message}");
            Environment.ExitCode = 1;
            return;
        }

        Console.WriteLine(token);
        return;
    }

    var builder = WebApplication.CreateBuilder(args);

    builder.Host.UseSerilog((context, services, configuration) =>
    {
        configuration
            .ReadFrom.Configuration(context.Configuration)
            .ReadFrom.Services(services)
            // Capture WireHQ logs down to Debug so per-tenant diagnostic mode can selectively emit them; the
            // filter drops Debug/Verbose except for tenants whose diagnostic window is open (docs/15 §4).
            .MinimumLevel.Override("WireHQ", LogEventLevel.Debug)
            .Filter.With(new DiagnosticLogFilter(services.GetRequiredService<IDiagnosticModeStore>()))
            .Enrich.FromLogContext()
            .Enrich.WithEnvironmentName()
            .WriteTo.Console();

        // When a Collector is configured, also ship logs as OTLP (docs/15 §4). The sink stamps each record
        // with the current Activity's trace/span id, so logs in Loki join the traces in Tempo by the same
        // trace id that is the correlation reference (ADR-030). Console stays for dev. Off otherwise.
        var otlpEndpoint = context.Configuration["OpenTelemetry:OtlpEndpoint"];
        if (!string.IsNullOrWhiteSpace(otlpEndpoint))
        {
            configuration.WriteTo.OpenTelemetry(options =>
            {
                options.Endpoint = otlpEndpoint;
                options.Protocol = OtlpProtocol.Grpc;
                options.ResourceAttributes = ObservabilityResource.Attributes(context.Configuration);
            });
        }
    });

    // Composition root: each layer registers itself, so this reads like a table of contents.
    builder.Services
        .AddApplication()
        .AddInfrastructure(builder.Configuration)
        .AddIdentityServices(builder.Configuration)
        // Licence-token signing/verification for the Marketplace (docs/19, ADR-036). Lazy: the key
        // ring is only built when first resolved, so deployments without a Licensing section (a
        // self-hosted install, a dev stack that never touches licensing) need no keys configured.
        .AddLicensing(builder.Configuration)
        .AddApiServices(builder.Configuration)
        // The CE Marketplace module-activation runtime (docs/29, ADR-046). A no-op in SaaS; the Community
        // Edition overlays this seam with the real activation-store reader (+ Wave 3 call-home). Last in the
        // chain so the CE reader registration wins over the kept-core NoActivatedModules default.
        .AddActivatedModules(builder.Configuration);

    // The site-wide CAPTCHA verifier's outbound HttpClient (Cloudflare siteverify).
    builder.Services.AddHttpClient<ITurnstileVerifier, TurnstileVerifier>();

    // The webhook sender's outbound HttpClient (kept-core; docs/26 §8). A short timeout so a slow receiver can't
    // stall the outbox sweep (a timeout is a retryable failure); redirects are NOT followed (a receiver can't bounce
    // a signed delivery to another host); and — the real SSRF guard — a ConnectCallback resolves the destination and
    // connects only to a vetted PUBLIC address, so a tenant-supplied URL can't drive the server into loopback /
    // RFC1918 / link-local / cloud-metadata hosts (DNS rebinding is defeated because the vetted address is the one
    // connected to). Opt-out for tests/dev via Webhooks:AllowPrivateDestinations.
    builder.Services
        .AddHttpClient<WireHQ.Application.Abstractions.Webhooks.IWebhookTransport, WireHQ.Infrastructure.Webhooks.WebhookTransport>(
            client => client.Timeout = TimeSpan.FromSeconds(10))
        .ConfigurePrimaryHttpMessageHandler(serviceProvider =>
        {
            var allowPrivate = bool.TryParse(
                serviceProvider.GetRequiredService<IConfiguration>()["Webhooks:AllowPrivateDestinations"], out var v) && v;
            return new System.Net.Http.SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                ConnectCallback = async (context, cancellationToken) =>
                {
                    var addresses = await System.Net.Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken);
                    var target = allowPrivate
                        ? (addresses.Length > 0 ? addresses[0] : null)
                        : Array.Find(addresses, a => !WireHQ.Domain.Webhooks.WebhookAddressGuard.IsBlocked(a));
                    if (target is null)
                    {
                        throw new System.IO.IOException("Webhook destination resolves to a disallowed (private) address.");
                    }

                    var socket = new System.Net.Sockets.Socket(System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp)
                    {
                        NoDelay = true,
                    };
                    try
                    {
                        await socket.ConnectAsync(target, context.DnsEndPoint.Port, cancellationToken);
                        return new System.Net.Sockets.NetworkStream(socket, ownsSocket: true);
                    }
                    catch
                    {
                        socket.Dispose();
                        throw;
                    }
                },
            };
        });

    // A dedicated SSRF-guarded outbound HTTP client for notification channels (Chat webhooks now; SMS provider REST
    // later). Its OWN client re-declaring the same connect-time address guard + no-redirects + short timeout as the
    // webhook client above, so an operator-supplied chat/SMS URL can't drive the server into internal / cloud-metadata
    // hosts (docs/35 §4.3, blocker B-7). Opt-out for tests/dev via Notifications:AllowPrivateDestinations.
    builder.Services
        .AddHttpClient(WireHQ.Infrastructure.Messaging.Notifications.ChatChannel.HttpClientName,
            client => client.Timeout = TimeSpan.FromSeconds(10))
        .ConfigurePrimaryHttpMessageHandler(serviceProvider =>
        {
            var allowPrivate = bool.TryParse(
                serviceProvider.GetRequiredService<IConfiguration>()["Notifications:AllowPrivateDestinations"], out var v) && v;
            return new System.Net.Http.SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                ConnectCallback = async (context, cancellationToken) =>
                {
                    var addresses = await System.Net.Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken);
                    var target = allowPrivate
                        ? (addresses.Length > 0 ? addresses[0] : null)
                        : Array.Find(addresses, a => !WireHQ.Domain.Webhooks.WebhookAddressGuard.IsBlocked(a));
                    if (target is null)
                    {
                        throw new System.IO.IOException("Notification destination resolves to a disallowed (private) address.");
                    }

                    var socket = new System.Net.Sockets.Socket(System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp)
                    {
                        NoDelay = true,
                    };
                    try
                    {
                        await socket.ConnectAsync(target, context.DnsEndPoint.Port, cancellationToken);
                        return new System.Net.Sockets.NetworkStream(socket, ownsSocket: true);
                    }
                    catch
                    {
                        socket.Dispose();
                        throw;
                    }
                },
            };
        });

    // The module seam: discover + gate + wire feature modules. New modules are added here by
    // passing their assembly — no other host change required.
    builder.Services.AddModules(
        builder.Configuration,
        typeof(WireHQ.Modules.WireGuard.WireGuardModule).Assembly,
        typeof(WireHQ.Modules.Orchestration.OrchestrationModule).Assembly);

    // Scan the Api assembly for MediatR handlers — the composition layer is the one place that can both name a
    // module's domain events and reach a cross-cutting SaaS feature. Today that's the Access Policies
    // auto-recompile (Api/Policy, docs/22 §6); a no-op scan in the CE where that folder is stripped.
    builder.Services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(typeof(Program).Assembly));

    builder.Services.AddObservability(builder.Configuration);

    // Reverts entitlements when an app-side trial or a past-due grace window elapses (the only timed downgrade).

    // Nightly marketplace reconciliation watchdog: local orders vs Stripe payments → metrics + logs (docs/19 §4).

    // Physical audit retention: drops audit_logs partitions past the ceiling + re-anchors the hash chain. (ADR-031)
    builder.Services.AddHostedService<WireHQ.Api.Audit.AuditRetentionSweeperHostedService>();

    // Drains the webhook outbox: refreshes the subscription cache + sends due deliveries (HMAC + backoff).
    // Kept-core (webhooks are entitlement-gated core, K-2); idle until an endpoint exists. (docs/26 §8)
    builder.Services.AddHostedService<WireHQ.Api.Webhooks.WebhookSenderHostedService>();
    builder.Services.AddHostedService<WireHQ.Api.Notifications.NotificationSenderHostedService>();

    // Registers the business + fleet snapshot gauges (edition mix, agents, instances, peers). (docs/15 §7)

    // The public status self-probe: samples each component + keeps the daily uptime rollups fresh (docs/20 §4).

    // Runs due LDAP/AD directory syncs on a cadence (per-connection interval; docs/23 §12).

    // The agent mTLS gateway's dedicated Kestrel listener (opt-in; no-op when disabled). (ADR-028)
    builder.ConfigureAgentGateway();

    // HTTPS redirection is explicit opt-in (Https:RedirectPort). The API normally sits behind a TLS
    // edge (forwarded X-Forwarded-Proto makes requests read as https, so the middleware never fires),
    // but ASP.NET's PORT AUTO-DISCOVERY would pick the agent gateway's mTLS listener as the redirect
    // target the moment the gateway is enabled — 307-ing every plain-HTTP caller (a self-hosted LAN
    // install, in-network health probes) to a port that demands a client certificate.
    if (int.TryParse(builder.Configuration["Https:RedirectPort"], out var httpsRedirectPort))
    {
        builder.Services.AddHttpsRedirection(options => options.HttpsPort = httpsRedirectPort);
    }

    // The transactional emails' edition tagline (a self-hosted Community Edition sets
    // "Community Edition" so its mail is visually distinct from the hosted SaaS). A process-wide
    // constant set once here — see EmailTemplates.EditionTagline.
    WireHQ.Application.Common.Email.EmailTemplates.EditionTagline = builder.Configuration["Branding:EditionTagline"];

    var app = builder.Build();

    // Migrate-and-exit mode for the production migrations Job (see docs/10-deployment.md):
    //   dotnet WireHQ.Api.dll --migrate
    if (args.Contains("--migrate"))
    {
        await app.InitialiseDatabaseAsync();
        return;
    }

    // The licence-signing key ceremony (D-7, docs/19 §3): prints a fresh Ed25519 key pair as
    // ready-to-paste configuration, the private seed encrypted with THIS environment's
    // SecretProtection:Key. Run in the target environment; nothing is persisted.
    //   dotnet WireHQ.Api.dll --generate-licensing-key
    if (args.Contains("--generate-licensing-key"))
    {
        var protector = app.Services.GetRequiredService<WireHQ.Application.Abstractions.Security.ISecretProtector>();
        Console.WriteLine(WireHQ.Licensing.LicensingKeyCeremony.GenerateConfigBlock(protector, DateTimeOffset.UtcNow));
        return;
    }

    // Trust the reverse proxy's forwarded headers FIRST, so the rest of the pipeline sees the original
    // scheme + client IP. TLS terminates at the edge (the user's HAProxy/nginx → the web container's
    // nginx); without this the API only ever sees http, so the refresh cookie wouldn't be marked Secure
    // and the per-IP rate limiter would bucket every request under the proxy's address. The API is only
    // reachable through nginx, so we trust any forwarding hop (clear the default loopback-only allow-list).
    var forwardedHeaders = new ForwardedHeadersOptions
    {
        ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
    };
    forwardedHeaders.KnownNetworks.Clear();
    forwardedHeaders.KnownProxies.Clear();
    app.UseForwardedHeaders(forwardedHeaders);

    app.UseExceptionHandler();
    // The per-request summary line carries the correlation id + tenant + actor, so it is searchable
    // by org/user even for requests that short-circuit before tenant resolution (auth/rate-limit). (ADR-030)
    app.UseSerilogRequestLogging(options => options.EnrichDiagnosticContext = (diagnostic, httpContext) =>
    {
        diagnostic.Set("CorrelationId", CorrelationId.Current() ?? httpContext.TraceIdentifier);
        if (httpContext.RequestServices.GetService<ITenantContext>()?.OrganizationId is { } org)
        {
            diagnostic.Set("OrgId", org);
        }
        if (httpContext.RequestServices.GetService<ICurrentUser>()?.UserId is { } userId)
        {
            diagnostic.Set("UserId", userId);
        }
    });
    app.UseMiddleware<SecurityHeadersMiddleware>();

    // The OpenAPI reference (docs/27, ADR-044) is auth-gated BY DEFAULT IN EVERY ENVIRONMENT — the
    // Development-mode demo compose is a documented public-deployment path, so the environment name alone
    // must never weaken auth. Anonymous access is a local-only, explicit opt-in (both conditions required);
    // it also brings up the interactive dev UI, whose spec fetch only works where the JSON is anonymous.
    // The product's own reference UI is the SPA's Settings → API reference page. The JSON endpoint itself
    // is mapped with the other endpoints below.
    var openApiAnonymous = app.Environment.IsDevelopment()
        && app.Configuration.GetValue("OpenApi:AllowAnonymous", false);

    if (app.Environment.IsDevelopment())
    {
        await app.InitialiseDatabaseAsync();
    }
    else
    {
        app.UseHsts();
        // Only when explicitly configured — see the Https:RedirectPort note above the host build.
        if (int.TryParse(app.Configuration["Https:RedirectPort"], out _))
        {
            app.UseHttpsRedirection();
        }
    }

    app.UseCors();
    app.UseRateLimiter();

    app.UseAuthentication();
    app.UseMiddleware<TenantResolutionMiddleware>();
    // After auth + tenant resolution, so the org and user are known for log enrichment + the header.
    app.UseMiddleware<ObservabilityContextMiddleware>();
    app.UseAuthorization();

    app.MapControllers();
    app.MapModuleEndpoints();
    app.MapHealthEndpoints();

    // The versioned OpenAPI document, served from the process-lifetime cache (docs/27, O-1/O-2/O-7).
    // Endpoint-routed so it rides the nginx /api/ proxy, the rate limiter, and — via the default policy —
    // the smart JWT-or-API-key scheme: a key holder fetches the spec with the key itself. `OpenApi:Enabled`
    // (default on) is the kill switch; ExcludeFromDescription keeps the docs route out of its own document.
    if (app.Configuration.GetValue("OpenApi:Enabled", true))
    {
        var openApi = app.MapGet(
                "/api/docs/openapi/{documentName}.json",
                (HttpContext httpContext, Swashbuckle.AspNetCore.Swagger.ISwaggerProvider provider,
                 WireHQ.Api.Extensions.OpenApiSpecCache cache, string documentName) =>
                    cache.Serve(httpContext, provider, documentName))
            .ExcludeFromDescription();
        if (!openApiAnonymous)
        {
            openApi.RequireAuthorization();
        }

        // The interactive dev UI — only where the JSON it fetches is anonymous (see the O-2 note above).
        if (openApiAnonymous)
        {
            app.UseSwaggerUI(options =>
            {
                foreach (var description in app.DescribeApiVersions())
                {
                    options.SwaggerEndpoint(
                        $"/api/docs/openapi/{description.GroupName}.json", $"WireHQ API {description.GroupName}");
                }
            });
        }
    }

    await app.RunAsync();
}
catch (Exception ex) when (ex is not HostAbortedException)
{
    Log.Fatal(ex, "WireHQ API terminated unexpectedly");
    Environment.ExitCode = 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}

// Expose Program for WebApplicationFactory in integration tests.
public partial class Program;

internal static class ProgramExtensions
{
    public static IServiceCollection AddObservability(this IServiceCollection services, IConfiguration configuration)
    {
        // Redaction net for telemetry leaving the process (docs/15 §4). Registered as an ILogEventEnricher so
        // Serilog's ReadFrom.Services applies it to every sink (console + OTLP), scrubbing secrets from logs.
        services.AddSingleton<IRedactionPolicy, RedactionPolicy>();
        services.AddSingleton<ILogEventEnricher, RedactionEnricher>();
        // Per-tenant diagnostic verbosity (docs/15 §4): the store the DiagnosticLogFilter + the platform API share.
        services.AddSingleton<IDiagnosticModeStore, DiagnosticModeStore>();

        var otlpEndpoint = configuration["OpenTelemetry:OtlpEndpoint"];
        // Resource attributes tag every signal with the build + environment that produced it (docs/15 §14):
        // a trace/log/metric becomes attributable to a specific release. Shared with the Serilog OTLP logs sink
        // (ObservabilityResource) so traces and logs join in the backend.
        var environment = ObservabilityResource.Environment(configuration);
        // Data residency (docs/15 §14/§15): tag every span/metric with the deployment's region (when configured)
        // so a multi-region SaaS Collector can route + retain by region. Omitted when unset (single-region/self-host).
        var resourceAttributes = new List<KeyValuePair<string, object>>
        {
            new("deployment.environment", environment),
        };
        if (ObservabilityResource.Region(configuration) is { } region)
        {
            resourceAttributes.Add(new(ObservabilityResource.RegionAttribute, region));
        }

        var otel = services.AddOpenTelemetry()
            .ConfigureResource(resource => resource
                .AddService(serviceName: ObservabilityResource.ServiceName, serviceVersion: ObservabilityResource.Version)
                .AddAttributes(resourceAttributes))
            .WithTracing(tracing => tracing
                // A span per use case (TracingBehavior); without registering the source its activities aren't recorded.
                .AddSource(ApplicationTelemetry.ActivitySourceName)
                // Edge (agent) spans re-emitted by the gateway from the agent's step telemetry (docs/15 §9, Phase 5).
                .AddSource(AgentTelemetry.ActivitySourceName)
                // DB query visibility: Npgsql emits command spans on its built-in "Npgsql" ActivitySource (docs/15 §6).
                .AddSource("Npgsql")
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation())
            .WithMetrics(metrics => metrics
                // RED per use case (MetricsBehavior on the WireHQ.Application meter, docs/15 §7) — covers
                // background-dispatched use cases too, not just HTTP routes.
                .AddMeter(ApplicationMetrics.MeterName)
                // Deployment-engine metrics: job throughput/latency, dispatcher liveness, queue depth (docs/15 §7).
                .AddMeter(OrchestrationMetrics.MeterName)
                // Business + fleet snapshot gauges: edition mix, agents connected, instances/peers (docs/15 §7).
                // Marketplace commerce counters: checkouts/orders paid/reversed (docs/15 §19, docs/19 §4).
                // Idle in the Community Edition (the incrementing handlers are stripped).
                .AddMeter(MarketplaceMetrics.MeterName)
                // Status self-probe counters: sample cycles + observations + rollups (docs/20 §4/§10).
                // Idle in the Community Edition (the self-probe is stripped).
                .AddMeter(StatusMetrics.MeterName)
                // HTTP server/client RED (rate/errors/duration per route) comes free from these.
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                // Process/runtime infra: GC, heap, thread pool, exceptions (docs/15 §7 "runtime/infra").
                .AddRuntimeInstrumentation());

        if (!string.IsNullOrWhiteSpace(otlpEndpoint))
        {
            // Export to the configured Collector (self-host Grafana stack or SaaS — a config swap, docs/15 §14).
            // Passing the endpoint explicitly (vs. the OTEL_* env vars) keeps one config key in charge.
            otel.UseOtlpExporter(OtlpExportProtocol.Grpc, new Uri(otlpEndpoint));
        }

        // Dependency health (docs/15 §13): an operator-only snapshot of the services the API leans on. Surfaced
        // at GET /health/dependencies (platform-operator only); /health/{live,ready} stay simple for orchestration.
        services.AddHealthChecks()
            .AddCheck<DatabaseHealthCheck>("database")
            .AddCheck<SmtpHealthCheck>("smtp")
            .AddCheck<StripeHealthCheck>("stripe")
            .AddCheck<AgentGatewayHealthCheck>("agent_gateway")
            .AddCheck<OtlpCollectorHealthCheck>("otlp_collector");

        return services;
    }

    public static async Task InitialiseDatabaseAsync(this WebApplication app)
    {
        using var scope = app.Services.CreateScope();
        var initializer = scope.ServiceProvider.GetRequiredService<ApplicationDbContextInitializer>();
        await initializer.InitialiseAsync();

        // Seeders write reference data across tenants (demo org, system roles, content, …) with no request
        // context. Opt the seeding unit of work out of RLS — under PR1's FORCE the owner is subject too.
        // (ADR-027; in PR2 the seeders run as the owner, which bypasses RLS naturally.)
        scope.ServiceProvider.GetRequiredService<ISettableTenantContext>().SetBypass();
        await initializer.SeedAsync();
    }

    public static void MapHealthEndpoints(this WebApplication app)
    {
        // Liveness: the process is up.
        app.MapGet("/health/live", () => Results.Ok(new { status = "live" })).AllowAnonymous();

        // Readiness: the process can reach its database (gates traffic in K8s).
        app.MapGet("/health/ready", async (ApplicationDbContext db, CancellationToken ct) =>
            await db.Database.CanConnectAsync(ct)
                ? Results.Ok(new { status = "ready" })
                : Results.StatusCode(StatusCodes.Status503ServiceUnavailable)).AllowAnonymous();

        // Operator-only dependency snapshot (DB, SMTP, Stripe, agent gateway, Collector) with per-check detail —
        // a degraded optional integration is reported, never fatal. Platform-operator only (Super Admin or Support).
        app.MapHealthChecks("/health/dependencies", new HealthCheckOptions
        {
            ResponseWriter = HealthEndpoints.WriteDetailedJson,
        }).RequireAuthorization(HealthEndpoints.PlatformOperatorPolicy);
    }
}
