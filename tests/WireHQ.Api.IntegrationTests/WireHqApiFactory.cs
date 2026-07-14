using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Testcontainers.PostgreSql;
using WireHQ.Application.Abstractions;
using WireHQ.Application.Abstractions.Persistence;
using WireHQ.Application.Abstractions.Security;
using Xunit;

namespace WireHQ.Api.IntegrationTests;

/// <summary>
/// Spins up the full API against a real, disposable PostgreSQL (Testcontainers) so integration
/// tests exercise the genuine pipeline, EF mappings, and — crucially — tenant isolation. Runs in
/// the Development environment so the schema is created on startup. (docs/07-backend-structure.md)
/// Partial: the SaaS-only test fakes (the Stripe gateway) live in <c>WireHqApiFactory.Saas.cs</c>
/// so the Community Edition strip removes them file-wise (docs/17 §5).
/// </summary>
public sealed partial class WireHqApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .WithDatabase("wirehq")
        .WithUsername("wirehq")
        .WithPassword("wirehq")
        .Build();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        // Swap external adapters for deterministic fakes so tests never hit the network:
        //  • the Cloudflare verifier (accepts exactly FakeTurnstileVerifier.ValidToken)
        //  • the email sender (captures messages in-memory instead of talking to SMTP)
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ITurnstileVerifier>();
            services.AddSingleton<ITurnstileVerifier, FakeTurnstileVerifier>();

            services.RemoveAll<IEmailSender>();
            services.AddSingleton<IEmailSender, CapturingEmailSender>();

            // SaaS-only fakes (the Stripe gateway) register in the .Saas.cs partial; when the
            // Community Edition strip removes that file, this call compiles to nothing.
            ConfigureSaasTestFakes(services);
        });
    }

    /// <summary>Implemented in <c>WireHqApiFactory.Saas.cs</c>; absent (a no-op) in the Community Edition.</summary>
    partial void ConfigureSaasTestFakes(IServiceCollection services);

    /// <summary>
    /// A DI scope with the RLS tenant bypass set — for test setup/assertions that read or write
    /// tenant-owned tables directly (there's no HTTP request to establish an org, so RLS would otherwise
    /// fail-closed). Mirrors how trusted infrastructure (seeders, the dispatcher claim) opts out. (ADR-027)
    /// </summary>
    public IServiceScope CreateBypassScope()
    {
        var scope = Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ISettableTenantContext>().SetBypass();
        return scope;
    }

    /// <summary>
    /// Marks a user's email as verified (out-of-band) so tests can exercise the features behind the soft
    /// email-verification gate (creating VPN config, teams, invites). Does not rotate the security stamp,
    /// so any access token issued before this call stays valid.
    /// </summary>
    public async Task VerifyEmailAsync(string email)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
        var user = await db.Users.IgnoreQueryFilters().FirstAsync(u => u.Email.Value == email);
        user.VerifyEmail();
        await db.SaveChangesAsync(CancellationToken.None);
    }

    /// <summary>Sets an organisation's plan (edition) out-of-band — entitlement tests downgrade to Community to exercise gating.</summary>
    public async Task SetEditionAsync(Guid organizationId, WireHQ.Domain.Organizations.OrganizationEdition edition)
    {
        using var scope = Services.CreateScope();
        // The organizations root is RLS-protected; bypass for this cross-tenant write (the request scope is
        // unaffected — this is a throwaway scope). (ADR-027)
        scope.ServiceProvider.GetRequiredService<WireHQ.Application.Abstractions.ISettableTenantContext>().SetBypass();
        var db = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
        var org = await db.Organizations.IgnoreQueryFilters().FirstAsync(o => o.Id == organizationId);
        org.SetEdition(edition);
        await db.SaveChangesAsync(CancellationToken.None);
    }

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();

        // Point the API at the disposable Postgres (and pin test secrets) via environment variables.
        // WebApplication.CreateBuilder applies env vars *after* appsettings.*.json, so these reliably
        // win — unlike ConfigureAppConfiguration, which the minimal hosting model orders *before*
        // appsettings, leaving the API dialing the appsettings localhost:5432 instead of the
        // container's mapped port. Set here (after StartAsync, before the first CreateClient builds
        // the host) so the connection string is known and in place.
        //
        // Two roles (ADR-027): the container's bootstrap user (wirehq, a superuser) is the Admin/owner
        // connection that migrates + applies RLS; the app runs as the non-privileged, RLS-subject
        // wirehq_app role (the initializer creates it via rls.sql and syncs its password from Default).
        // Running the suite as wirehq_app is what proves the bypass allow-list is complete end-to-end.
        var adminConnectionString = _postgres.GetConnectionString();
        var appConnectionString = new NpgsqlConnectionStringBuilder(adminConnectionString)
        {
            Username = "wirehq_app",
            Password = "wirehq_app",
        }.ConnectionString;
        Environment.SetEnvironmentVariable("ConnectionStrings__Admin", adminConnectionString);
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", appConnectionString);
        Environment.SetEnvironmentVariable("Jwt__SigningKey", "integration-test-signing-key-at-least-32-bytes-long!!");
        Environment.SetEnvironmentVariable("SecretProtection__Key", "ZGV2LW9ubHktMzItYnl0ZS1hZXMta2V5LWNoYW5nZSE=");
        Environment.SetEnvironmentVariable("Seed__DemoData", "false");
        // New orgs default to Enterprise in the suite so the functional tests run unconstrained by plan
        // caps/feature gates; the entitlement tests downgrade their org to Community via SetEditionAsync to
        // exercise gating. Production defaults to Community. (docs/commercial.md)
        Environment.SetEnvironmentVariable("Entitlements__DefaultEdition", "Enterprise");
        // Ship-on-by-default, but OFF for the suite so the existing auth/platform tests don't need a
        // CAPTCHA token. The captcha-enforcement test enables it explicitly, then restores it.
        Environment.SetEnvironmentVariable("Turnstile__EnabledByDefault", "false");
        // The status self-probe writes to the shared status schema on a timer; disable the background loop so
        // it can't race the StatusProbe tests, which drive the probe directly. (No-op in the CE — no probe.)
        Environment.SetEnvironmentVariable("Status__Probe__Enabled", "false");
        // The directory-sync scheduler runs due syncs on a timer; disable the background loop so it can't race
        // the directory tests, which drive the scheduler directly. (No-op in the CE — no scheduler.)
        Environment.SetEnvironmentVariable("Directory__Sync__Enabled", "false");
        // The webhook sender drains the outbox on a timer; disable the background loop so it can't race the webhook
        // tests, which drive the dispatch scheduler directly. (Kept-core — present in every edition.)
        Environment.SetEnvironmentVariable("Webhooks__Sender__Enabled", "false");
        // The notification sender expands jobs + drains deliveries on a timer; disable the background loop so it can't
        // race the notification tests, which drive the dispatch scheduler directly. (Kept-core — every edition.)
        Environment.SetEnvironmentVariable("Notifications__Sender__Enabled", "false");
        // Allow the tests' localhost chat stub sink through the notification channels' SSRF guard (private/loopback
        // destinations are blocked by default in prod), mirroring the webhook flag below.
        Environment.SetEnvironmentVariable("Notifications__AllowPrivateDestinations", "true");
        // Allow the tests' localhost stub sink through the sender's SSRF guard (private/loopback destinations are
        // blocked by default in prod). The SSRF-rejection test sets it back to false for its own run.
        Environment.SetEnvironmentVariable("Webhooks__AllowPrivateDestinations", "true");

        // The in-memory TestServer sets no RemoteIpAddress, so every auth request across the whole
        // shared-factory suite lands in the single "anonymous" rate-limit partition (default 10/min) —
        // enough register/login calls trip a 429. Lift the limits for tests only; production keeps the
        // real values from appsettings. (See G-15: the integration suite shares one factory.)
        Environment.SetEnvironmentVariable("RateLimiting__AuthPermitPerMinute", "100000");
        Environment.SetEnvironmentVariable("RateLimiting__GlobalPermitPerMinute", "100000");
    }

    // Explicit implementation avoids clashing with WebApplicationFactory's own ValueTask DisposeAsync().
    async Task IAsyncLifetime.DisposeAsync()
    {
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", null);
        Environment.SetEnvironmentVariable("ConnectionStrings__Admin", null);
        Environment.SetEnvironmentVariable("Jwt__SigningKey", null);
        Environment.SetEnvironmentVariable("SecretProtection__Key", null);
        Environment.SetEnvironmentVariable("Seed__DemoData", null);
        Environment.SetEnvironmentVariable("Turnstile__EnabledByDefault", null);
        Environment.SetEnvironmentVariable("Status__Probe__Enabled", null);
        Environment.SetEnvironmentVariable("Directory__Sync__Enabled", null);
        Environment.SetEnvironmentVariable("Webhooks__Sender__Enabled", null);
        Environment.SetEnvironmentVariable("Webhooks__AllowPrivateDestinations", null);
        Environment.SetEnvironmentVariable("RateLimiting__AuthPermitPerMinute", null);
        Environment.SetEnvironmentVariable("RateLimiting__GlobalPermitPerMinute", null);
        await _postgres.DisposeAsync();
        await base.DisposeAsync();
    }
}

/// <summary>A deterministic Turnstile verifier for tests — accepts exactly <see cref="ValidToken"/>.</summary>
public sealed class FakeTurnstileVerifier : ITurnstileVerifier
{
    public const string ValidToken = "valid-turnstile-token";

    public Task<bool> VerifyAsync(string secret, string? token, string? remoteIp, CancellationToken cancellationToken = default) =>
        Task.FromResult(token == ValidToken);
}

/// <summary>Captures sent email in-memory (a singleton shared across the suite) so tests can assert on it.</summary>
public sealed class CapturingEmailSender : IEmailSender
{
    private readonly List<EmailMessage> _sent = [];

    public IReadOnlyList<EmailMessage> Sent
    {
        get { lock (_sent) { return _sent.ToList(); } }
    }

    /// <summary>The most recent message sent to <paramref name="to"/> (tests use unique recipients).</summary>
    public EmailMessage? LastTo(string to)
    {
        lock (_sent) { return _sent.LastOrDefault(m => string.Equals(m.To, to, StringComparison.OrdinalIgnoreCase)); }
    }

    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        lock (_sent) { _sent.Add(message); }
        return Task.CompletedTask;
    }
}

// NB: the SaaS-only test fakes (the Stripe gateway) live in WireHqApiFactory.Saas.cs — the
// Community Edition strip removes that file (docs/17 §5).
