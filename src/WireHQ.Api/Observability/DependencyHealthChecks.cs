using System.Net.Sockets;
using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using WireHQ.Application.Abstractions.Persistence;
using WireHQ.Infrastructure.Persistence;

namespace WireHQ.Api.Observability;

/// <summary>
/// Dependency health checks (docs/15 §13): beyond liveness/readiness, an operator-only snapshot of the
/// services the API leans on — the database, the configured SMTP + Stripe integrations, the agent mTLS
/// gateway, and the OTLP Collector. Surfaced at <c>GET /health/dependencies</c> (platform-operator only);
/// each check degrades gracefully (a missing optional integration is reported, never fatal).
/// </summary>
public static class HealthEndpoints
{
    public const string PlatformOperatorPolicy = "PlatformOperator";

    /// <summary>Writes the aggregate + per-check report as JSON for the detailed operator endpoint.</summary>
    public static Task WriteDetailedJson(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json";
        var payload = new
        {
            status = report.Status.ToString(),
            totalDurationMs = report.TotalDuration.TotalMilliseconds,
            checks = report.Entries.Select(e => new
            {
                name = e.Key,
                status = e.Value.Status.ToString(),
                description = e.Value.Description,
                durationMs = e.Value.Duration.TotalMilliseconds,
            }),
        };
        return context.Response.WriteAsync(JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }
}

/// <summary>Can the API reach its Postgres database? (The same probe as readiness, surfaced with detail.)</summary>
public sealed class DatabaseHealthCheck(ApplicationDbContext dbContext) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        await dbContext.Database.CanConnectAsync(cancellationToken)
            ? HealthCheckResult.Healthy("Connected.")
            : HealthCheckResult.Unhealthy("Cannot reach the database.");
}

/// <summary>Is SMTP configured? Unconfigured is Degraded (email falls back to the dev log sink), not fatal.</summary>
public sealed class SmtpHealthCheck(IApplicationDbContext dbContext) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var settings = await dbContext.PlatformSettings.AsNoTracking().FirstOrDefaultAsync(cancellationToken);
        return settings is { SmtpEnabled: true, SmtpConfigured: true }
            ? HealthCheckResult.Healthy($"SMTP configured ({settings.SmtpHost}:{settings.SmtpPort}).")
            : HealthCheckResult.Degraded("SMTP is not enabled/configured — transactional email logs to the dev sink.");
    }
}

/// <summary>Is Stripe billing configured? Unconfigured is Degraded (self-serve billing inactive), not fatal.</summary>
public sealed class StripeHealthCheck(IApplicationDbContext dbContext) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var settings = await dbContext.PlatformSettings.AsNoTracking().FirstOrDefaultAsync(cancellationToken);
        return settings is { StripeEnabled: true, StripeConfigured: true }
            ? HealthCheckResult.Healthy("Stripe billing configured.")
            : HealthCheckResult.Degraded("Stripe is not enabled/configured — self-serve billing is inactive.");
    }
}

/// <summary>The agent mTLS gateway's config state (off by default). A disabled gateway is a valid state.</summary>
public sealed class AgentGatewayHealthCheck(IConfiguration configuration) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var enabled = configuration.GetValue<bool>("AgentGateway:Enabled");
        var port = configuration.GetValue("AgentGateway:Port", 28443);
        return Task.FromResult(enabled
            ? HealthCheckResult.Healthy($"Agent gateway enabled (mTLS listener on :{port}).")
            : HealthCheckResult.Healthy("Agent gateway disabled (config-only / SSH deployments)."));
    }
}

/// <summary>The OTLP Collector: not configured = telemetry export off (Healthy); configured-but-unreachable = Degraded.</summary>
public sealed class OtlpCollectorHealthCheck(IConfiguration configuration) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var endpoint = configuration["OpenTelemetry:OtlpEndpoint"];
        if (string.IsNullOrWhiteSpace(endpoint) || !Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
        {
            return HealthCheckResult.Healthy("OTLP export disabled (no Collector endpoint configured).");
        }

        try
        {
            using var client = new TcpClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            await client.ConnectAsync(uri.Host, uri.Port, timeout.Token);
            return HealthCheckResult.Healthy($"Collector reachable at {uri.Host}:{uri.Port}.");
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            return HealthCheckResult.Degraded($"Collector configured but unreachable at {uri.Host}:{uri.Port} — telemetry may be dropping.");
        }
    }
}
