using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace WireHQ.Modules.Orchestration.Services;

/// <summary>
/// Background loop that runs the <see cref="IStatusReconciler"/> on a fixed cadence
/// (<c>Modules:Orchestration:Reconciler:IntervalSeconds</c>, default 45s, clamped 5–3600). Each pass gets
/// its own scope; a failed pass is logged and the loop continues. Single-instance for now — HA needs a
/// claim guard like the dispatcher (gap #5). (docs/12-remote-orchestration.md §10)
/// </summary>
public sealed class StatusReconcilerHostedService(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<StatusReconcilerHostedService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = ResolveInterval(configuration);
        logger.LogInformation("Status reconciler started (every {Seconds:0}s).", interval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var reconciler = scope.ServiceProvider.GetRequiredService<IStatusReconciler>();
                await reconciler.ReconcileAllAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Status reconciler pass failed; backing off.");
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

        logger.LogInformation("Status reconciler stopped.");
    }

    // Indexer + int.TryParse (not IConfiguration.GetValue<T>, the Binder extension — G-06).
    private static TimeSpan ResolveInterval(IConfiguration configuration)
    {
        var raw = configuration["Modules:Orchestration:Reconciler:IntervalSeconds"];
        var seconds = int.TryParse(raw, out var value) ? value : 45;
        return TimeSpan.FromSeconds(Math.Clamp(seconds, 5, 3600));
    }
}
