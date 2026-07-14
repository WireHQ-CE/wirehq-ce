namespace WireHQ.Api.Updates;

/// <summary>
/// The Community Edition update-check poller (docs/30 U-3). Runs the FIRST check promptly after boot (a short
/// fixed delay, so a restart doesn't leave a security banner blank for long), then re-checks every
/// <c>Updates:CheckIntervalHours</c> (default 24) with a random jitter added only to the steady-state schedule so
/// installs don't all hit the manifest host at the same instant. Fail-soft (a failed check leaves the last-known
/// status). Disabled by <c>Updates:Enabled=false</c> for air-gapped installs. CE-ONLY (registered by the CE
/// <c>AddActivatedModules</c> seam, so SaaS never polls).
/// </summary>
public sealed class UpdateCheckHostedService(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<UpdateCheckHostedService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!Enabled(configuration))
        {
            logger.LogInformation("Update check is disabled (Updates:Enabled=false).");
            return;
        }

        var interval = ResolveInterval(configuration);

        // Prompt first check (jitter only applies to subsequent checks).
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        logger.LogInformation("Update check started (every {Hours:0}h).", interval.TotalHours);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Update check failed; backing off.");
            }

            var jitter = TimeSpan.FromSeconds(Random.Shared.Next(0, 3600));
            try
            {
                await Task.Delay(interval + jitter, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        logger.LogInformation("Update check stopped.");
    }

    private async Task CheckOnceAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var client = scope.ServiceProvider.GetRequiredService<SignedManifestClient>();
        var provider = scope.ServiceProvider.GetRequiredService<PolledUpdateStatusProvider>();

        var manifest = await client.FetchAsync(cancellationToken);
        if (manifest is not null)
        {
            provider.Record(manifest);
        }
    }

    private static bool Enabled(IConfiguration configuration) =>
        !string.Equals(configuration["Updates:Enabled"], "false", StringComparison.OrdinalIgnoreCase);

    private static TimeSpan ResolveInterval(IConfiguration configuration)
    {
        var raw = configuration["Updates:CheckIntervalHours"];
        var hours = int.TryParse(raw, out var value) ? value : 24;
        return TimeSpan.FromHours(Math.Clamp(hours, 1, 168));
    }
}
