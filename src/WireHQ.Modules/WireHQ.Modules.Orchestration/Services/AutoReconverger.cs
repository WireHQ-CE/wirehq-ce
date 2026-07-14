using Microsoft.EntityFrameworkCore;
using WireHQ.Application.Abstractions;
using WireHQ.Application.Abstractions.Persistence;
using WireHQ.Application.Entitlements;
using WireHQ.Domain.Auditing;
using WireHQ.Domain.Organizations;
using WireHQ.Modules.Orchestration.Domain;
using WireHQ.Modules.WireGuard.Domain;
using WireHQ.Shared.Observability;

namespace WireHQ.Modules.Orchestration.Services;

/// <summary>
/// Re-converges a drifted instance by enqueuing a redeploy — the opt-in <c>AutoReconverge</c> remediation
/// (docs/12 §13 Phase 3, gap #4). Invoked from the drift-detection paths (the SSH reconciler + the
/// agent-reported status endpoint). The actual deploy is driven by the existing job engine.
/// </summary>
public interface IAutoReconverger
{
    /// <summary>
    /// Enqueues a <c>DeployConfig</c> job for a drifted instance, unless one is already in flight or one was
    /// created very recently (the cooldown) — so a target that drifts + fails to converge can't thrash. Adds
    /// the job to the unit of work; the caller saves.
    /// </summary>
    Task TryEnqueueAsync(Guid organizationId, Guid instanceId, CancellationToken cancellationToken);
}

public sealed class AutoReconverger(
    IApplicationDbContext dbContext, IDateTimeProvider clock, IAuditWriter audit, IEffectiveFeatureSet effectiveFeatures)
    : IAutoReconverger
{
    // Don't auto-redeploy more often than this — a drifted-but-failing target would otherwise re-enqueue
    // every reconcile cycle. A manual deploy also resets the window (any recent job counts).
    private static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(5);

    public async Task TryEnqueueAsync(Guid organizationId, Guid instanceId, CancellationToken cancellationToken)
    {
        // MM-14 deactivation guard (docs/33 §5.4): the drift paths gate on the PERSISTED `DeploymentTarget.AutoReconverge`
        // flag, which stays true after a customer deactivates the auto-reconverge module (self-host) or downgrades the
        // plan (SaaS). Re-check the live entitlement union before enqueuing a redeploy. Uses the edition-param check keyed
        // on the explicit `organizationId` (not the ambient tenant) so it holds for both callers. `organizations` is not
        // ITenantOwned (no org RLS), so a plain read resolves under any context; the soft-delete filter stays on so a
        // deleted org is treated as not-entitled. Fail-closed.
        var edition = await dbContext.Organizations
            .Where(o => o.Id == organizationId)
            .Select(o => (OrganizationEdition?)o.Edition)
            .FirstOrDefaultAsync(cancellationToken);
        if (edition is null
            || !await effectiveFeatures.HasFeatureAsync(edition.Value, PlanFeatures.DriftAutoReconverge, cancellationToken))
        {
            return;
        }

        var now = clock.UtcNow;
        var cooldownStart = now - Cooldown;

        var recentOrInFlight = await dbContext.Set<DeploymentJob>().AnyAsync(j =>
            j.InstanceId == instanceId &&
            (j.Status == DeploymentJobStatus.Pending
                || j.Status == DeploymentJobStatus.Dispatched
                || j.Status == DeploymentJobStatus.Applying
                || j.CreatedAtUtc >= cooldownStart),
            cancellationToken);
        if (recentOrInFlight)
        {
            return;
        }

        var desiredConfigVersion = await dbContext.Set<ConfigVersion>()
            .Where(c => c.TargetType == ConfigTargetType.Instance && c.TargetId == instanceId)
            .MaxAsync(c => (int?)c.Version, cancellationToken);

        var job = DeploymentJob.Queue(
            organizationId, instanceId, DeploymentJobType.DeployConfig, desiredConfigVersion,
            $"auto-reconverge:{instanceId:N}:{Guid.NewGuid():N}", CorrelationId.Current(), now,
            reconvergeReason: "Config drift detected on the instance.");
        dbContext.Set<DeploymentJob>().Add(job);

        audit.Record("deployment.auto_reconverge", AuditOutcome.Success, nameof(DeploymentJob), job.Id.ToString(),
            new { instanceId, reason = "config drift" });
    }
}
