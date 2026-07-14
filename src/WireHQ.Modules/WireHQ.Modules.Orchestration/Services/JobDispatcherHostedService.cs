using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WireHQ.Application.Abstractions;
using WireHQ.Application.Abstractions.Persistence;
using WireHQ.Modules.Orchestration.Domain;
using WireHQ.Modules.Orchestration.Observability;

namespace WireHQ.Modules.Orchestration.Services;

/// <summary>
/// Background loop that drains the deployment-job queue. It resolves a fresh scope per job (so each job
/// gets its own DbContext + tenant scope, with no cross-job bleed), drains while there's work, then
/// idles for <see cref="PollInterval"/>. Failures are logged and the loop continues — a bad job never
/// stops the engine. (docs/12-remote-orchestration.md §4)
/// </summary>
public sealed class JobDispatcherHostedService(IServiceScopeFactory scopeFactory, ILogger<JobDispatcherHostedService> logger)
    : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Deployment job dispatcher started.");

        // Pending-queue depth as an observable gauge (docs/15 §7): the metrics SDK samples it on its own
        // collection cycle, scoping a short-lived DbContext to count queued jobs across tenants.
        OrchestrationMetrics.RegisterQueueDepthGauge(CountPendingJobs);

        while (!stoppingToken.IsCancellationRequested)
        {
            // Liveness: one tick per pass — a non-zero rate means the engine is alive (docs/15 §7/§13).
            OrchestrationMetrics.DispatcherRuns.Add(1);

            bool didWork;
            try
            {
                using var scope = scopeFactory.CreateScope();
                var dispatcher = scope.ServiceProvider.GetRequiredService<IJobDispatcher>();
                didWork = await dispatcher.ProcessNextAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Deployment dispatcher pass failed; backing off.");
                didWork = false;
            }

            if (!didWork)
            {
                try
                {
                    await Task.Delay(PollInterval, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        logger.LogInformation("Deployment job dispatcher stopped.");
    }

    /// <summary>Count jobs still queued, across all tenants. A system read with no request scope → RLS bypass.</summary>
    private long CountPendingJobs()
    {
        using var scope = scopeFactory.CreateScope();
        scope.ServiceProvider.GetRequiredService<ISettableTenantContext>().SetBypass();
        var dbContext = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
        return dbContext.Set<DeploymentJob>()
            .IgnoreQueryFilters()
            .LongCount(j => j.Status == DeploymentJobStatus.Pending);
    }
}
