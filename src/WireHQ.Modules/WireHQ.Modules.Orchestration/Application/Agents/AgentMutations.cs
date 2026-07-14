using Microsoft.EntityFrameworkCore;
using WireHQ.Application.Abstractions;
using WireHQ.Application.Abstractions.Persistence;
using WireHQ.Application.Common.Messaging;
using WireHQ.Domain.Auditing;
using WireHQ.Modules.Orchestration.Authorization;
using WireHQ.Modules.Orchestration.Domain;
using WireHQ.Shared.Results;

namespace WireHQ.Modules.Orchestration.Application.Agents;

/// <summary>Temporarily suspends an agent — the gateway rejects its certificate until it is reactivated.</summary>
public sealed record DisableAgentCommand(Guid Id) : ICommand, IAuthorizedRequest
{
    public IReadOnlyCollection<string> RequiredPermissions => [OrchestrationPermissions.Agents.Manage];
}

public sealed class DisableAgentCommandHandler(IApplicationDbContext dbContext, IAuditWriter audit)
    : ICommandHandler<DisableAgentCommand>
{
    public async Task<Result> Handle(DisableAgentCommand command, CancellationToken cancellationToken)
    {
        var agent = await dbContext.Set<Agent>().FirstOrDefaultAsync(a => a.Id == command.Id, cancellationToken);
        if (agent is null)
        {
            return OrchestrationErrors.Agent.NotFound;
        }

        agent.Disable();
        audit.Record("orch.agent.disabled", AuditOutcome.Success, nameof(Agent), agent.Id.ToString());
        return Result.Success();
    }
}

/// <summary>Re-enables a disabled agent (no effect on a revoked one — revocation is permanent).</summary>
public sealed record ReactivateAgentCommand(Guid Id) : ICommand, IAuthorizedRequest
{
    public IReadOnlyCollection<string> RequiredPermissions => [OrchestrationPermissions.Agents.Manage];
}

public sealed class ReactivateAgentCommandHandler(IApplicationDbContext dbContext, IAuditWriter audit)
    : ICommandHandler<ReactivateAgentCommand>
{
    public async Task<Result> Handle(ReactivateAgentCommand command, CancellationToken cancellationToken)
    {
        var agent = await dbContext.Set<Agent>().FirstOrDefaultAsync(a => a.Id == command.Id, cancellationToken);
        if (agent is null)
        {
            return OrchestrationErrors.Agent.NotFound;
        }

        agent.Reactivate();
        audit.Record("orch.agent.reactivated", AuditOutcome.Success, nameof(Agent), agent.Id.ToString());
        return Result.Success();
    }
}

/// <summary>Permanently revokes an agent — its certificate is rejected forever (the no-CRL kill switch).</summary>
public sealed record RevokeAgentCommand(Guid Id) : ICommand, IAuthorizedRequest
{
    public IReadOnlyCollection<string> RequiredPermissions => [OrchestrationPermissions.Agents.Manage];
}

public sealed class RevokeAgentCommandHandler(IApplicationDbContext dbContext, IAuditWriter audit)
    : ICommandHandler<RevokeAgentCommand>
{
    public async Task<Result> Handle(RevokeAgentCommand command, CancellationToken cancellationToken)
    {
        var agent = await dbContext.Set<Agent>().FirstOrDefaultAsync(a => a.Id == command.Id, cancellationToken);
        if (agent is null)
        {
            return OrchestrationErrors.Agent.NotFound;
        }

        agent.Revoke();
        audit.Record("orch.agent.revoked", AuditOutcome.Success, nameof(Agent), agent.Id.ToString());
        return Result.Success();
    }
}
