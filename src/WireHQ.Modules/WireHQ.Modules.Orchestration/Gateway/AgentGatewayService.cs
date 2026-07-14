using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WireHQ.Application.Abstractions;
using WireHQ.Application.Abstractions.Persistence;
using WireHQ.Domain.Auditing;
using WireHQ.Modules.Orchestration.Certificates;
using WireHQ.Modules.Orchestration.Domain;
using WireHQ.Modules.Orchestration.Observability;
using WireHQ.Modules.Orchestration.Services;
using WireHQ.Modules.WireGuard.Domain;
using WireHQ.Modules.WireGuard.Services;
using WireHQ.Shared.Results;

namespace WireHQ.Modules.Orchestration.Gateway;

public sealed record EnrollAgentRequest(string Token, string Csr, string? Name, string? Platform);
public sealed record EnrollAgentResponse(Guid AgentId, string CertificatePem, string CaCertificatePem, DateTimeOffset NotAfterUtc);

public sealed record RotateCertRequest(string Csr);
public sealed record RotateCertResponse(string CertificatePem, string CaCertificatePem, DateTimeOffset NotAfterUtc);

public sealed record HeartbeatRequest(string? Version);
public sealed record HeartbeatResponse(string Status);

/// <summary>
/// A signed deployment bundle for one job. The agent verifies <see cref="Signature"/> before applying.
/// When <see cref="AgentManaged"/> is true the bundle carries no <c>PrivateKey</c> line — the agent injects
/// its locally-held interface key after verification and reports the public key in the result. (ADR-028)
/// <see cref="CorrelationId"/> carries the originating deploy's correlation id (the W3C trace id) to the edge,
/// so the agent's logs and reports chain back to the request that triggered the deploy. (ADR-030, docs/15 §3)
/// </summary>
public sealed record AgentJobDto(Guid JobId, Guid InstanceId, string InterfaceName, string Bundle, string Signature, bool AgentManaged, string? CorrelationId);

/// <summary>
/// The agent's report for a job. <see cref="InterfacePublicKey"/> is set on a successful
/// <c>AgentManaged</c> apply so WireHQ can record the agent-generated interface public key.
/// <see cref="CorrelationId"/> echoes the deploy's correlation id back (the gateway also holds it on the job
/// row — it is authoritative server-side — so this round-trips the spine to the edge and back). (ADR-030)
/// </summary>
public sealed record AgentJobResultRequest(string Status, string? AppliedConfigHash, string? Error, string? InterfacePublicKey, string? CorrelationId = null);

public sealed record AgentTelemetryRequest(Guid InstanceId, IReadOnlyList<AgentPeerTelemetry> Peers);
public sealed record AgentPeerTelemetry(string PublicKey, DateTimeOffset? LastHandshakeAtUtc, long RxBytes, long TxBytes, string? Endpoint);

/// <summary>The agent's observed runtime status for its managed interfaces — config-drift is agent-computed
/// (current on-disk config vs the config it last applied), since WireHQ can't reach a Pull host inline.</summary>
public sealed record AgentStatusRequest(IReadOnlyList<AgentInstanceStatus> Instances);
public sealed record AgentInstanceStatus(Guid InstanceId, string? ConfigHash, bool Drift);

/// <summary>
/// A batch of the agent's structured step events for one activity (a job apply, or a poll tick), forwarded to
/// the telemetry plane (docs/15 §9, Phase 5). The gateway re-emits these as OTel spans + logs tagged with the
/// agent's cert identity. <see cref="JobId"/> scopes the batch to a deploy job (the gateway then uses that job's
/// authoritative correlation id as the trace parent); otherwise <see cref="CorrelationId"/> (if a valid trace
/// id) is used. Best-effort: bad/oversized batches are clamped, never fatal.
/// </summary>
public sealed record AgentDiagnosticsRequest(
    Guid? JobId,
    Guid? InstanceId,
    string? CorrelationId,
    IReadOnlyList<AgentDiagnosticEvent> Events);

/// <summary>An opaque agent task the gateway hands to the agent (a directory sync is the first kind, wave 3). The
/// agent switches on <see cref="Kind"/> and interprets <see cref="PayloadJson"/> (never carries secrets — D-3).</summary>
public sealed record AgentTaskDto(Guid TaskId, string Kind, string PayloadJson);

/// <summary>The agent's result for a task — an opaque JSON body the owning provider interprets (e.g. an LDAP snapshot).</summary>
public sealed record AgentTaskResultRequest(System.Text.Json.JsonElement Result);

/// <summary>
/// The data-plane logic behind the agent gateway (<c>/agent/v1/*</c>). Deliberately outside the MediatR
/// pipeline (no JWT request to scope from): it owns tenant scoping explicitly via
/// <see cref="ISettableTenantContext"/>, mirroring the dispatcher's cross-tenant claim. Persists through the
/// request-scoped <see cref="IApplicationDbContext"/> (it calls SaveChanges itself — there is no unit-of-work
/// behaviour here). (ADR-028)
/// </summary>
public sealed class AgentGatewayService(
    IApplicationDbContext dbContext,
    ISettableTenantContext tenant,
    IServiceScopeFactory scopeFactory,
    ICertificateAuthority certificateAuthority,
    IServerConfigRenderer renderer,
    IBundleSigner bundleSigner,
    IKeyManagementService keys,
    IAutoReconverger autoReconverger,
    IDateTimeProvider clock,
    IAuditWriter audit,
    IEnumerable<WireHQ.Application.Abstractions.Orchestration.IAgentTaskProvider> taskProviders,
    ILogger<AgentGatewayService> logger)
{
    /// <summary>Guards against an abusive batch — extra events past this are dropped.</summary>
    private const int MaxDiagnosticEvents = 200;

    /// <summary>Redeems a single-use token + a CSR into a signed client cert, creating the agent. Pre-cert.</summary>
    public async Task<Result<EnrollAgentResponse>> EnrollAsync(EnrollAgentRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Token) || string.IsNullOrWhiteSpace(request.Csr))
        {
            return OrchestrationErrors.Agent.EnrollmentRejected;
        }

        var now = clock.UtcNow;
        var tokenHash = AgentEnrollmentToken.HashToken(request.Token);

        // Find the token's org cross-tenant in a throwaway bypass scope (the request scope stays un-bypassed).
        Guid organizationId;
        Guid tokenId;
        using (var lookupScope = scopeFactory.CreateScope())
        {
            lookupScope.ServiceProvider.GetRequiredService<ISettableTenantContext>().SetBypass();
            var lookupDb = lookupScope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
            var token = await lookupDb.Set<AgentEnrollmentToken>()
                .IgnoreQueryFilters()
                .AsNoTracking()
                .FirstOrDefaultAsync(t => t.TokenHash == tokenHash, cancellationToken);
            if (token is null || !token.IsRedeemable(now))
            {
                return OrchestrationErrors.Agent.EnrollmentRejected;
            }

            organizationId = token.OrganizationId;
            tokenId = token.Id;
        }

        // Scope the unit of work to the token's org, then BURN the token atomically (single-use under
        // concurrency: only one caller's conditional UPDATE affects a row). Burn-before-issue is the safe
        // failure mode — a spent token on a later error beats a replayable one.
        tenant.SetTenant(organizationId);
        var burned = await dbContext.Set<AgentEnrollmentToken>()
            .Where(t => t.Id == tokenId && t.UsedAtUtc == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.UsedAtUtc, now), cancellationToken);
        if (burned != 1)
        {
            return OrchestrationErrors.Agent.EnrollmentRejected;
        }

        var agentId = Guid.CreateVersion7();
        var issued = await certificateAuthority.IssueClientCertificateAsync(organizationId, agentId, request.Csr, cancellationToken);
        if (issued.IsFailure)
        {
            return issued.Error;
        }

        var name = string.IsNullOrWhiteSpace(request.Name) ? $"agent-{agentId.ToString()[..8]}" : request.Name.Trim();
        var agentResult = Agent.Enroll(
            agentId, organizationId, name, issued.Value.Sha256Fingerprint, issued.Value.CertificatePem, request.Platform, now);
        if (agentResult.IsFailure)
        {
            return agentResult.Error;
        }

        dbContext.Set<Agent>().Add(agentResult.Value);
        audit.Record("orch.agent.enrolled", AuditOutcome.Success, nameof(Agent), agentId.ToString(),
            new { name, request.Platform });
        await dbContext.SaveChangesAsync(cancellationToken);

        return new EnrollAgentResponse(agentId, issued.Value.CertificatePem, issued.Value.CaCertificatePem, issued.Value.NotAfterUtc);
    }

    /// <summary>Re-keys an authenticated agent before its certificate expires (same CA, new leaf).</summary>
    public async Task<Result<RotateCertResponse>> RotateAsync(Guid agentId, Guid organizationId, RotateCertRequest request, CancellationToken cancellationToken)
    {
        var agent = await dbContext.Set<Agent>().FirstOrDefaultAsync(a => a.Id == agentId, cancellationToken);
        if (agent is null || !agent.IsActive)
        {
            return OrchestrationErrors.Agent.NotFound;
        }

        var issued = await certificateAuthority.IssueClientCertificateAsync(organizationId, agentId, request.Csr, cancellationToken);
        if (issued.IsFailure)
        {
            return issued.Error;
        }

        agent.RotateCertificate(issued.Value.Sha256Fingerprint, issued.Value.CertificatePem);
        audit.Record("orch.agent.cert_rotated", AuditOutcome.Success, nameof(Agent), agentId.ToString());
        await dbContext.SaveChangesAsync(cancellationToken);

        return new RotateCertResponse(issued.Value.CertificatePem, issued.Value.CaCertificatePem, issued.Value.NotAfterUtc);
    }

    /// <summary>Records an authenticated agent's liveness + reported version.</summary>
    public async Task<Result<HeartbeatResponse>> HeartbeatAsync(Guid agentId, HeartbeatRequest request, CancellationToken cancellationToken)
    {
        var agent = await dbContext.Set<Agent>().FirstOrDefaultAsync(a => a.Id == agentId, cancellationToken);
        if (agent is null || !agent.IsActive)
        {
            return OrchestrationErrors.Agent.NotFound;
        }

        agent.Heartbeat(request.Version, clock.UtcNow);
        await dbContext.SaveChangesAsync(cancellationToken);

        return new HeartbeatResponse("ok");
    }

    /// <summary>
    /// Hands the agent the <c>Dispatched</c> jobs for instances bound to it: each as a freshly-rendered,
    /// WireHQ-<b>signed</b> config bundle. Marks the handed jobs <c>Applying</c> (the agent posts a result to
    /// close them). Tenant scope (set by the auth handler) + the AgentId filter keep an agent to its own org's
    /// bound instances. (ADR-028)
    /// </summary>
    public async Task<Result<IReadOnlyList<AgentJobDto>>> GetJobsAsync(Guid agentId, CancellationToken cancellationToken)
    {
        var bindings = await dbContext.Set<DeploymentTarget>()
            .Where(t => t.Kind == DeploymentTargetKind.Agent && t.AgentId == agentId)
            .Select(t => new { t.InstanceId, t.InterfaceName, t.KeyCustody })
            .ToListAsync(cancellationToken);
        if (bindings.Count == 0)
        {
            return Result.Success<IReadOnlyList<AgentJobDto>>([]);
        }

        var instanceIds = bindings.Select(b => b.InstanceId).ToList();
        var jobs = await dbContext.Set<DeploymentJob>()
            .Where(j => instanceIds.Contains(j.InstanceId) && j.Status == DeploymentJobStatus.Dispatched)
            .OrderBy(j => j.CreatedAtUtc)
            .ToListAsync(cancellationToken);

        var delivered = new List<AgentJobDto>(jobs.Count);
        foreach (var job in jobs)
        {
            var binding = bindings.First(b => b.InstanceId == job.InstanceId);
            var instance = await dbContext.Set<WireGuardInstance>().FirstOrDefaultAsync(i => i.Id == job.InstanceId, cancellationToken);
            if (instance is null)
            {
                job.Fail(clock.UtcNow, "Instance no longer exists.");
                continue;
            }

            // AgentManaged renders key-less (no server PrivateKey leaves WireHQ); the agent injects its own.
            var rendered = await renderer.RenderAsync(instance, binding.InterfaceName, binding.KeyCustody, cancellationToken);
            if (rendered.IsFailure)
            {
                job.Fail(clock.UtcNow, rendered.Error.Description);
                continue;
            }

            var signature = await bundleSigner.SignAsync(job.OrganizationId, Encoding.UTF8.GetBytes(rendered.Value.ConfigText), cancellationToken);
            job.MarkApplying(clock.UtcNow);
            delivered.Add(new AgentJobDto(
                job.Id, job.InstanceId, binding.InterfaceName, rendered.Value.ConfigText, signature,
                binding.KeyCustody == KeyCustody.AgentManaged, job.CorrelationId));
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return delivered;
    }

    /// <summary>Closes a job from the agent's applied result (Succeeded/Failed). The job's instance must be bound to this agent.</summary>
    public async Task<Result> PostJobResultAsync(Guid agentId, Guid jobId, AgentJobResultRequest request, CancellationToken cancellationToken)
    {
        var job = await dbContext.Set<DeploymentJob>().FirstOrDefaultAsync(j => j.Id == jobId, cancellationToken);
        if (job is null)
        {
            return OrchestrationErrors.Deployment.JobNotFound;
        }

        var binding = await dbContext.Set<DeploymentTarget>()
            .FirstOrDefaultAsync(t => t.InstanceId == job.InstanceId && t.Kind == DeploymentTargetKind.Agent && t.AgentId == agentId, cancellationToken);
        if (binding is null)
        {
            return OrchestrationErrors.Deployment.JobNotFound;
        }

        var succeeded = string.Equals(request.Status, nameof(DeploymentJobStatus.Succeeded), StringComparison.OrdinalIgnoreCase);
        if (succeeded)
        {
            job.Succeed(clock.UtcNow, "Applied by agent.");

            // AgentManaged: adopt the agent-generated interface public key and scrub any WireHQ-held private
            // key, so WireHQ holds only the public key for this instance. (ADR-028)
            if (binding.KeyCustody == KeyCustody.AgentManaged && !string.IsNullOrWhiteSpace(request.InterfacePublicKey))
            {
                await AdoptAgentKeyAsync(job.InstanceId, request.InterfacePublicKey!, job.CorrelationId, cancellationToken);
            }
        }
        else
        {
            job.Fail(clock.UtcNow, string.IsNullOrWhiteSpace(request.Error) ? "Agent reported failure." : request.Error);
        }

        // Chain this audit to the originating deploy's correlation (held authoritatively on the job row) rather
        // than the agent's poll request — so a support lookup by the deploy's reference finds the whole chain,
        // browser → API → job → agent → back. (ADR-030, docs/15 §3)
        audit.Record("orch.agent.job_result", AuditOutcome.Success, nameof(DeploymentJob), jobId.ToString(), new { request.Status }, job.CorrelationId);
        await dbContext.SaveChangesAsync(cancellationToken);

        // Agent-target jobs reach their terminal state here (not in the dispatcher) — record throughput + latency.
        OrchestrationMetrics.RecordCompleted(job);
        return Result.Success();
    }

    // Records the agent-generated interface public key on the instance and removes the now-orphaned WireHQ
    // key material (idempotent: a no-op once the key is already adopted). A malformed key is ignored — the
    // deploy still succeeded; the operator simply won't get a usable client export until a valid key lands.
    private async Task AdoptAgentKeyAsync(Guid instanceId, string publicKey, string? correlationId, CancellationToken cancellationToken)
    {
        if (!IsValidWireGuardKey(publicKey))
        {
            return;
        }

        var instance = await dbContext.Set<WireGuardInstance>().FirstOrDefaultAsync(i => i.Id == instanceId, cancellationToken);
        if (instance is null || (instance.PrivateKeyId is null && instance.PublicKey == publicKey.Trim()))
        {
            return;
        }

        if (instance.AdoptAgentManagedKey(publicKey).IsSuccess)
        {
            await keys.DeleteForOwnerAsync(KeyOwnerType.Instance, instanceId, cancellationToken);
            audit.Record("orch.agent.key_adopted", AuditOutcome.Success, nameof(WireGuardInstance), instanceId.ToString(), correlationId: correlationId);
        }
    }

    // A WireGuard key is base64 of 32 raw bytes.
    private static bool IsValidWireGuardKey(string value)
    {
        Span<byte> buffer = stackalloc byte[32];
        return Convert.TryFromBase64String(value.Trim(), buffer, out var written) && written == 32;
    }

    /// <summary>
    /// The generic non-WireGuard task channel (ADR-040): aggregates the pending tasks every registered
    /// <see cref="WireHQ.Application.Abstractions.Orchestration.IAgentTaskProvider"/> has queued for this agent.
    /// The gateway stays ignorant of what a task is (LDAP sync is the first). In the CE no provider is
    /// registered, so this returns an empty list — a harmless idle seam.
    /// </summary>
    public async Task<Result<IReadOnlyList<AgentTaskDto>>> GetTasksAsync(Guid agentId, Guid organizationId, CancellationToken cancellationToken)
    {
        var tasks = new List<AgentTaskDto>();
        foreach (var provider in taskProviders)
        {
            foreach (var task in await provider.GetPendingAsync(organizationId, agentId, cancellationToken))
            {
                tasks.Add(new AgentTaskDto(task.TaskId, task.Kind, task.PayloadJson));
            }
        }

        return tasks;
    }

    /// <summary>Routes a posted task result to whichever provider owns the task (they answer <c>NotFound</c> for a
    /// task they don't own, so the gateway tries the next). Opaque JSON in, no gateway knowledge of the payload.</summary>
    public async Task<Result> PostTaskResultAsync(Guid agentId, Guid organizationId, Guid taskId, string resultJson, CancellationToken cancellationToken)
    {
        foreach (var provider in taskProviders)
        {
            var result = await provider.SubmitResultAsync(organizationId, agentId, taskId, resultJson, cancellationToken);
            if (result.IsSuccess || result.Error.Type != ErrorType.NotFound)
            {
                return result;
            }
        }

        return OrchestrationErrors.Deployment.JobNotFound;
    }

    /// <summary>
    /// Records live peer telemetry the agent observed (<c>wg show dump</c>) — handshake + transfer per peer,
    /// matched by public key. Same <see cref="Peer.UpdateTelemetry"/> sink the SSH reconciler feeds. The
    /// reported instance must be bound to this agent.
    /// </summary>
    public async Task<Result> PostTelemetryAsync(Guid agentId, AgentTelemetryRequest request, CancellationToken cancellationToken)
    {
        var boundToThisAgent = await dbContext.Set<DeploymentTarget>()
            .AnyAsync(t => t.InstanceId == request.InstanceId && t.Kind == DeploymentTargetKind.Agent && t.AgentId == agentId, cancellationToken);
        if (!boundToThisAgent)
        {
            return OrchestrationErrors.Deployment.InstanceNotFound;
        }

        var peers = await dbContext.Set<Peer>()
            .Where(p => p.InstanceId == request.InstanceId)
            .ToListAsync(cancellationToken);

        var now = clock.UtcNow;
        foreach (var reported in request.Peers ?? [])
        {
            peers.FirstOrDefault(p => p.PublicKey == reported.PublicKey)
                ?.UpdateTelemetry(reported.LastHandshakeAtUtc, reported.RxBytes, reported.TxBytes, reported.Endpoint, now);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    /// <summary>
    /// Records the agent's observed runtime status for its managed interfaces — the agent computes drift
    /// locally (current on-disk config vs what it last applied) and reports it here, so the same
    /// <see cref="InstanceRuntimeStatus"/> + Deployment-panel drift badge that the SSH reconciler feeds also
    /// works for Pull (agent) instances, which WireHQ cannot probe inline. (ADR-028)
    /// </summary>
    public async Task<Result> PostStatusAsync(Guid agentId, AgentStatusRequest request, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        foreach (var reported in request.Instances ?? [])
        {
            var binding = await dbContext.Set<DeploymentTarget>()
                .FirstOrDefaultAsync(t => t.InstanceId == reported.InstanceId && t.Kind == DeploymentTargetKind.Agent && t.AgentId == agentId, cancellationToken);
            if (binding is null)
            {
                continue;
            }

            var instance = await dbContext.Set<WireGuardInstance>().FirstOrDefaultAsync(i => i.Id == reported.InstanceId, cancellationToken);
            if (instance is null)
            {
                continue;
            }

            var runtime = await dbContext.Set<InstanceRuntimeStatus>().FirstOrDefaultAsync(r => r.InstanceId == reported.InstanceId, cancellationToken);
            if (runtime is null)
            {
                runtime = InstanceRuntimeStatus.Create(instance.OrganizationId, instance.Id);
                dbContext.Set<InstanceRuntimeStatus>().Add(runtime);
            }

            var driftDetail = reported.Drift ? "The agent reports the deployed config differs from the config it last applied." : null;
            runtime.Record(reported.Drift ? "Degraded" : "Running", desiredHash: null, actualHash: reported.ConfigHash, hasDrift: reported.Drift, driftDetail, now);
            instance.ChangeStatus(reported.Drift ? InstanceStatus.Degraded : InstanceStatus.Running, now);

            // Opt-in remediation: re-enqueue a deploy so the agent re-applies the desired config on its next poll.
            if (reported.Drift && binding.AutoReconverge)
            {
                await autoReconverger.TryEnqueueAsync(instance.OrganizationId, instance.Id, cancellationToken);
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    /// <summary>
    /// Forwards the agent's structured step events to the telemetry plane (docs/15 §9, Phase 5): one OTel span
    /// per batch on the <see cref="AgentTelemetry"/> source (the steps become span events), plus a structured log
    /// per event — both tenant/agent-tagged from the cert (never from the body). When the batch names a deploy
    /// <c>JobId</c> owned by this agent, the span is parented to that job's <b>authoritative</b> correlation id
    /// (held server-side on the job row), so the edge steps nest under the browser → API → job trace (ADR-030);
    /// otherwise a supplied valid trace id is used, else it is a root span. Best-effort: nothing here fails the
    /// agent's poll, but ownership is enforced (an agent can only tag jobs/instances bound to it).
    /// </summary>
    public async Task<Result> PostDiagnosticsAsync(
        Guid agentId, Guid organizationId, AgentDiagnosticsRequest request, CancellationToken cancellationToken)
    {
        var events = (request.Events ?? []).Take(MaxDiagnosticEvents).ToList();
        if (events.Count == 0)
        {
            return Result.Success();
        }

        // Ownership + the authoritative correlation id come from the server, not the agent's body.
        string? traceId = null;
        Guid? instanceId = request.InstanceId;
        if (request.JobId is { } jobId)
        {
            var job = await dbContext.Set<DeploymentJob>().AsNoTracking().FirstOrDefaultAsync(j => j.Id == jobId, cancellationToken);
            var owned = job is not null && await dbContext.Set<DeploymentTarget>().AnyAsync(
                t => t.InstanceId == job.InstanceId && t.Kind == DeploymentTargetKind.Agent && t.AgentId == agentId, cancellationToken);
            if (!owned)
            {
                return OrchestrationErrors.Deployment.JobNotFound;
            }

            traceId = job!.CorrelationId; // authoritative deploy trace id
            instanceId = job.InstanceId;
        }
        else if (instanceId is { } iid)
        {
            var owned = await dbContext.Set<DeploymentTarget>().AnyAsync(
                t => t.InstanceId == iid && t.Kind == DeploymentTargetKind.Agent && t.AgentId == agentId, cancellationToken);
            if (!owned)
            {
                return OrchestrationErrors.Deployment.InstanceNotFound;
            }

            traceId = request.CorrelationId;
        }
        else
        {
            traceId = request.CorrelationId;
        }

        AgentTelemetryEmitter.EmitSpan(agentId, organizationId, instanceId, request.JobId, traceId, events);
        LogEvents(agentId, organizationId, instanceId, request.JobId, events);
        return Result.Success();
    }

    // A structured log per event (ships as OTLP via Serilog) — the "visible without SSH" self-diagnostics view.
    private void LogEvents(
        Guid agentId, Guid organizationId, Guid? instanceId, Guid? jobId, IReadOnlyList<AgentDiagnosticEvent> events)
    {
        using var scope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["AgentId"] = agentId,
            ["OrganizationId"] = organizationId,
            ["InstanceId"] = instanceId,
            ["JobId"] = jobId,
        });

        foreach (var e in events)
        {
            logger.Log(
                MapLevel(e.Level, e.Outcome),
                "Agent step {AgentEvent} {Outcome} ({DurationMs}ms){AgentMessage}",
                e.Name,
                e.Outcome ?? "ok",
                e.DurationMs ?? 0,
                string.IsNullOrWhiteSpace(e.Message) ? string.Empty : $": {e.Message}");
        }
    }

    private static LogLevel MapLevel(string? level, string? outcome)
    {
        if (AgentTelemetryEmitter.IsFailure(outcome))
        {
            return LogLevel.Error;
        }

        return level?.ToLowerInvariant() switch
        {
            "error" => LogLevel.Error,
            "warn" or "warning" => LogLevel.Warning,
            "debug" => LogLevel.Debug,
            _ => LogLevel.Information,
        };
    }
}
