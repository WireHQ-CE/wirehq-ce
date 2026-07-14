using FluentValidation;
using Microsoft.EntityFrameworkCore;
using WireHQ.Application.Abstractions;
using WireHQ.Application.Abstractions.Persistence;
using WireHQ.Application.Common.Messaging;
using WireHQ.Application.Entitlements;
using WireHQ.Domain.Auditing;
using WireHQ.Modules.Orchestration.Domain;
using WireHQ.Modules.WireGuard.Authorization;
using WireHQ.Modules.WireGuard.Domain;
using WireHQ.Modules.WireGuard.Services;
using WireHQ.Shared.Results;

namespace WireHQ.Modules.Orchestration.Application.Targets;

/// <summary>
/// Binds an instance to where it deploys: <c>Local</c> (config-only), <c>Ssh</c> (a registered SSH target),
/// or <c>Agent</c> (an enrolled outbound-only agent that drains its jobs on poll). One binding per instance
/// (upserted). (docs/12-remote-orchestration.md §4, ADR-028)
/// </summary>
public sealed record BindInstanceTargetCommand(
    Guid InstanceId,
    string Kind,
    Guid? SshTargetId,
    Guid? AgentId,
    string? KeyCustody,
    string? InterfaceName,
    bool? AutoReconverge) : ICommand, IAuthorizedRequest
{
    public IReadOnlyCollection<string> RequiredPermissions => [WireGuardPermissions.Instances.Manage];
}

public sealed class BindInstanceTargetCommandValidator : AbstractValidator<BindInstanceTargetCommand>
{
    public BindInstanceTargetCommandValidator()
    {
        RuleFor(x => x.Kind).Must(k => Enum.TryParse<DeploymentTargetKind>(k, ignoreCase: true, out _))
            .WithMessage("Kind must be 'Local', 'Ssh', or 'Agent'.");
        RuleFor(x => x.SshTargetId).NotNull()
            .When(x => string.Equals(x.Kind, nameof(DeploymentTargetKind.Ssh), StringComparison.OrdinalIgnoreCase))
            .WithMessage("An SSH target id is required when binding to SSH.");
        RuleFor(x => x.AgentId).NotNull()
            .When(x => string.Equals(x.Kind, nameof(DeploymentTargetKind.Agent), StringComparison.OrdinalIgnoreCase))
            .WithMessage("An agent id is required when binding to an agent.");
        RuleFor(x => x.KeyCustody).Must(k => Enum.TryParse<KeyCustody>(k, ignoreCase: true, out _))
            .When(x => !string.IsNullOrEmpty(x.KeyCustody))
            .WithMessage("KeyCustody must be 'WireHqManaged' or 'AgentManaged'.");
    }
}

public sealed class BindInstanceTargetCommandHandler(
    IApplicationDbContext dbContext,
    ITenantContext tenant,
    IKeyManagementService keys,
    IEntitlementService entitlements,
    IAuditWriter audit)
    : ICommandHandler<BindInstanceTargetCommand>
{
    public async Task<Result> Handle(BindInstanceTargetCommand command, CancellationToken cancellationToken)
    {
        if (tenant.OrganizationId is not { } organizationId)
        {
            return Error.Forbidden("auth.tenant_required", "No active organization.");
        }

        var kind = Enum.Parse<DeploymentTargetKind>(command.Kind, ignoreCase: true);

        var instance = await dbContext.Set<WireGuardInstance>()
            .FirstOrDefaultAsync(i => i.Id == command.InstanceId, cancellationToken);
        if (instance is null)
        {
            return OrchestrationErrors.Deployment.InstanceNotFound;
        }

        if (kind == DeploymentTargetKind.Ssh)
        {
            var exists = await dbContext.Set<SshTarget>().AnyAsync(t => t.Id == command.SshTargetId, cancellationToken);
            if (!exists)
            {
                return OrchestrationErrors.SshTarget.NotFound;
            }
        }

        if (kind == DeploymentTargetKind.Agent)
        {
            var active = await dbContext.Set<Agent>()
                .AnyAsync(a => a.Id == command.AgentId && a.Status == AgentStatus.Active, cancellationToken);
            if (!active)
            {
                return OrchestrationErrors.Agent.NotFound;
            }
        }

        var autoReconverge = command.AutoReconverge ?? false;

        // Plan gating. Auto-re-converge is a paid feature; the number of remote gateways (SSH/Agent-bound
        // instances) is plan-capped — binding this instance only consumes a slot if it isn't already remote.
        if (autoReconverge && !await entitlements.HasFeatureAsync(PlanFeatures.DriftAutoReconverge, cancellationToken))
        {
            return Error.Forbidden("plan.upgrade_required", "Auto re-converge is not included in your plan. Upgrade your plan to enable it.");
        }

        if (kind != DeploymentTargetKind.Local)
        {
            var otherGateways = await dbContext.Set<DeploymentTarget>()
                .CountAsync(t => t.InstanceId != instance.Id && t.Kind != DeploymentTargetKind.Local, cancellationToken);
            var withinQuota = await entitlements.EnsureCanAddAsync(PlanResource.Gateways, otherGateways, cancellationToken);
            if (withinQuota.IsFailure)
            {
                return withinQuota.Error;
            }
        }

        var target = await dbContext.Set<DeploymentTarget>()
            .FirstOrDefaultAsync(t => t.InstanceId == instance.Id, cancellationToken);
        if (target is null)
        {
            target = DeploymentTarget.Create(organizationId, instance.Id);
            dbContext.Set<DeploymentTarget>().Add(target);
        }

        var custody = command.KeyCustody is { } c ? Enum.Parse<KeyCustody>(c, ignoreCase: true) : KeyCustody.WireHqManaged;

        // Every binding except AgentManaged needs a WireHQ-held interface key. If we're leaving an
        // already-adopted AgentManaged instance (its private key was scrubbed), re-key under WireHQ custody
        // so deploy/export work again — the next deploy pushes the fresh key. (ADR-028)
        var needsWireHqKey = kind != DeploymentTargetKind.Agent || custody == KeyCustody.WireHqManaged;
        if (needsWireHqKey && instance.PrivateKeyId is null)
        {
            var rekeyed = keys.GenerateAndStoreKeyPair(organizationId, KeyOwnerType.Instance, instance.Id);
            instance.AdoptWireHqManagedKey(rekeyed.PublicKey, rekeyed.KeyMaterialId);
        }

        switch (kind)
        {
            case DeploymentTargetKind.Ssh:
                target.BindSsh(command.SshTargetId!.Value, command.InterfaceName, autoReconverge);
                break;
            case DeploymentTargetKind.Agent:
                target.BindAgent(command.AgentId!.Value, custody, command.InterfaceName, autoReconverge);
                break;
            default:
                target.BindLocal();
                break;
        }

        audit.Record("orch.target.bound", AuditOutcome.Success, nameof(DeploymentTarget), instance.Id.ToString(),
            new { kind = kind.ToString(), command.SshTargetId, command.AgentId, target.InterfaceName });

        return Result.Success();
    }
}
