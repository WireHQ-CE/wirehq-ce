using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WireHQ.Application.Abstractions;
using WireHQ.Application.Abstractions.Persistence;
using WireHQ.Modules.Orchestration.Domain;

namespace WireHQ.Modules.Orchestration.Services;

/// <summary>Pulls live status for every remote (status-capable) instance and persists the telemetry.</summary>
public interface IStatusReconciler
{
    Task ReconcileAllAsync(CancellationToken cancellationToken);
}

/// <summary>
/// One reconciliation pass: lists SSH-bound instances <b>across all tenants</b> (a system process, so it
/// bypasses the tenant query filter), then reconciles each in its <b>own DI scope</b> — fresh DbContext +
/// tenant context — so one unreachable host (or a poisoned change-tracker) can never stall or contaminate
/// the others. Mirrors the <c>JobDispatcher</c> background-tenant pattern (G-21). (docs/12 §10)
/// </summary>
public sealed class StatusReconciler(IServiceScopeFactory scopeFactory, ILogger<StatusReconciler> logger)
    : IStatusReconciler
{
    public async Task ReconcileAllAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<(Guid OrganizationId, Guid InstanceId)> targets;
        using (var scope = scopeFactory.CreateScope())
        {
            // Cross-tenant listing with no org — opt out of RLS for this read too (mirrors the L1
            // IgnoreQueryFilters); each per-instance scope below re-scopes via SetTenant. (ADR-027)
            scope.ServiceProvider.GetRequiredService<ISettableTenantContext>().SetBypass();
            var db = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
            var rows = await db.Set<DeploymentTarget>()
                .IgnoreQueryFilters()
                .Where(t => t.Kind == DeploymentTargetKind.Ssh)
                .Select(t => new { t.OrganizationId, t.InstanceId })
                .ToListAsync(cancellationToken);
            targets = rows.Select(r => (r.OrganizationId, r.InstanceId)).ToList();
        }

        foreach (var (organizationId, instanceId) in targets)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            try
            {
                using var scope = scopeFactory.CreateScope();
                var services = scope.ServiceProvider;
                services.GetRequiredService<ISettableTenantContext>().SetTenant(organizationId);

                var result = await services.GetRequiredService<IInstanceStatusSync>().SyncAsync(instanceId, cancellationToken);
                if (result.IsFailure)
                {
                    // Expected operationally (host down / wg absent) — the on-demand endpoint surfaces it to the operator.
                    logger.LogDebug("Status reconcile skipped instance {InstanceId}: {Error}", instanceId, result.Error.Description);
                    continue;
                }

                await services.GetRequiredService<IApplicationDbContext>().SaveChangesAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Status reconcile failed for instance {InstanceId}.", instanceId);
            }
        }
    }
}
