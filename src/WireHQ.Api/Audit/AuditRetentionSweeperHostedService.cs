using WireHQ.Application.Abstractions;

namespace WireHQ.Api.Audit;

/// <summary>
/// Background loop that runs the <see cref="IAuditRetentionService"/> on a fixed cadence
/// (<c>Audit:RetentionSweeper:IntervalSeconds</c>, default 86400s = daily, clamped 3600–604800). Each pass
/// gets its own scope; a failed pass is logged and the loop continues. Single-instance for now (the work is
/// idempotent, so duplicate runs are harmless). Mirrors <c>SubscriptionGraceSweeperHostedService</c>.
/// (ADR-031, docs/15 §5)
/// </summary>
public sealed class AuditRetentionSweeperHostedService(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<AuditRetentionSweeperHostedService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = ResolveInterval(configuration);
        logger.LogInformation("Audit retention sweeper started (every {Seconds:0}s).", interval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                await scope.ServiceProvider.GetRequiredService<IAuditRetentionService>().SweepAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Audit retention sweep failed; backing off.");
            }

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        logger.LogInformation("Audit retention sweeper stopped.");
    }

    // Indexer + int.TryParse (not the Binder's GetValue<T> extension — G-06).
    private static TimeSpan ResolveInterval(IConfiguration configuration)
    {
        var raw = configuration["Audit:RetentionSweeper:IntervalSeconds"];
        var seconds = int.TryParse(raw, out var value) ? value : 86400;
        return TimeSpan.FromSeconds(Math.Clamp(seconds, 3600, 604800));
    }
}
