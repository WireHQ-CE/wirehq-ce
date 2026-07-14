using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WireHQ.Application.Abstractions;
using WireHQ.Application.Abstractions.Persistence;
using WireHQ.Application.Abstractions.Security;
using WireHQ.Modules.Orchestration.Domain;
using WireHQ.Modules.Orchestration.Observability;
using WireHQ.Modules.WireGuard.Domain;
using WireHQ.Modules.WireGuard.Providers;
using WireHQ.Shared.Results;

namespace WireHQ.Modules.Orchestration.Services;

/// <summary>Drives one pending deployment job to a terminal state. Scoped (own DbContext + tenant scope).</summary>
public interface IJobDispatcher
{
    /// <summary>Claims and processes the next pending job. Returns false when the queue is empty.</summary>
    Task<bool> ProcessNextAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The deployment-job engine. It claims the oldest pending job <b>across all tenants</b> (a system
/// process, so it bypasses the tenant query filter), then establishes that job's tenant for the rest of
/// processing and enacts it through the instance's <see cref="IWireGuardProvider"/> according to the
/// provider's <see cref="ProviderExecutionModel"/>. For the config-only Local provider
/// (<see cref="ProviderExecutionModel.None"/>) the job is a no-op success — but the whole pipeline
/// (claim → apply → record timeline) runs and is observable. (docs/12-remote-orchestration.md §4)
///
/// Single-dispatcher safe. Horizontal scale needs a <c>FOR UPDATE SKIP LOCKED</c> claim (a later
/// hardening) so two dispatchers never grab the same job.
/// </summary>
public sealed class JobDispatcher(
    IApplicationDbContext dbContext,
    ISettableTenantContext tenant,
    IWireGuardProviderFactory providerFactory,
    IServerConfigRenderer renderer,
    ISecretProtector secretProtector,
    IDateTimeProvider clock,
    ILogger<JobDispatcher> logger)
    : IJobDispatcher
{
    public async Task<bool> ProcessNextAsync(CancellationToken cancellationToken)
    {
        // Claim the oldest pending job across all tenants. There's no request/org here, so opt out of the
        // RLS tenant policy for the claim too (mirrors the IgnoreQueryFilters L1 opt-out); SetTenant below
        // re-enables RLS for the rest of processing. (G-21, ADR-027)
        tenant.SetBypass();

        var job = await dbContext.Set<DeploymentJob>()
            .IgnoreQueryFilters()
            .Where(j => j.Status == DeploymentJobStatus.Pending)
            .OrderBy(j => j.CreatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);
        if (job is null)
        {
            return false;
        }

        // From here on, run inside the job's tenant so the query filter + RLS + stamping scope correctly.
        tenant.SetTenant(job.OrganizationId);

        // Restore the originating request's correlation id (+ job/org) into the log scope, so this
        // background execution's logs chain back to the request that enqueued the job. (ADR-030, G-21)
        using var _ = logger.BeginScope(new Dictionary<string, object?>
        {
            ["CorrelationId"] = job.CorrelationId,
            ["JobId"] = job.Id,
            ["OrgId"] = job.OrganizationId,
        });

        job.MarkDispatched(clock.UtcNow);
        await dbContext.SaveChangesAsync(cancellationToken);

        try
        {
            var instance = await dbContext.Set<WireGuardInstance>()
                .FirstOrDefaultAsync(i => i.Id == job.InstanceId, cancellationToken);
            if (instance is null)
            {
                job.Fail(clock.UtcNow, "Instance no longer exists.");
            }
            else
            {
                await EnactAsync(job, instance, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Deployment job {JobId} failed to apply", job.Id);
            job.Fail(clock.UtcNow, ex.Message);
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        // Throughput + latency for a job the dispatcher drove to a terminal state inline (Local/SSH). Agent
        // targets returned early still Dispatched — they complete via the gateway, recorded there. (docs/15 §7)
        OrchestrationMetrics.RecordCompleted(job);
        return true;
    }

    private async Task EnactAsync(DeploymentJob job, WireGuardInstance instance, CancellationToken cancellationToken)
    {
        // The instance's deployment binding (orch) decides where + how it deploys — Local by default.
        var binding = await dbContext.Set<DeploymentTarget>()
            .FirstOrDefaultAsync(t => t.InstanceId == instance.Id, cancellationToken);
        var kind = binding?.Kind ?? DeploymentTargetKind.Local;

        // Pull (agent) targets are enacted by the agent on its next outbound poll — not inline. Leave the
        // job Dispatched (already marked when claimed); the gateway hands the agent the signed bundle and
        // owns the rest of its lifecycle (Applying → Succeeded/Failed via POST result). (ADR-028)
        if (kind == DeploymentTargetKind.Agent)
        {
            return;
        }

        job.MarkApplying(clock.UtcNow);

        switch (kind)
        {
            case DeploymentTargetKind.Local:
                job.Succeed(clock.UtcNow, "Config-only (Local) — desired state is the source of truth; nothing to deploy.");
                return;

            case DeploymentTargetKind.Ssh:
                await DeployOverSshAsync(job, instance, binding!, cancellationToken);
                return;
        }
    }

    private async Task DeployOverSshAsync(DeploymentJob job, WireGuardInstance instance, DeploymentTarget binding, CancellationToken cancellationToken)
    {
        var sshTarget = binding.SshTargetId is { } id
            ? await dbContext.Set<SshTarget>().FirstOrDefaultAsync(t => t.Id == id, cancellationToken)
            : null;
        if (sshTarget is null)
        {
            job.Fail(clock.UtcNow, "The bound SSH target is missing.");
            return;
        }

        var provider = providerFactory.Resolve(WireGuardProviderType.SshLinux);
        var providerRef = new ProviderInstanceRef(instance.Id, instance.ExternalId, new Dictionary<string, string>
        {
            ["host"] = sshTarget.Host,
            ["port"] = sshTarget.Port.ToString(),
            ["username"] = sshTarget.Username,
            ["authKind"] = sshTarget.AuthKind.ToString(),
            ["credential"] = secretProtector.Unprotect(sshTarget.CredentialCiphertext),
            ["hostKeyFingerprint"] = sshTarget.HostKeyFingerprint ?? string.Empty,
        });

        var connectivity = await provider.TestConnectivityAsync(providerRef, cancellationToken);
        if (connectivity.IsFailure)
        {
            job.Fail(clock.UtcNow, connectivity.Error.Description);
            return;
        }

        var rendered = await renderer.RenderAsync(instance, binding.InterfaceName, binding.KeyCustody, cancellationToken);
        if (rendered.IsFailure)
        {
            job.Fail(clock.UtcNow, rendered.Error.Description);
            return;
        }

        var deploy = await provider.DeployConfigAsync(providerRef, rendered.Value, cancellationToken);
        if (deploy.IsFailure)
        {
            job.Fail(clock.UtcNow, deploy.Error.Description);
            return;
        }

        job.Succeed(clock.UtcNow, $"Deployed to {sshTarget.Host} (interface {binding.InterfaceName}).");
    }
}
