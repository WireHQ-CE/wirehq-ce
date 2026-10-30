using System.Security.Cryptography;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using WireHQ.Application.Abstractions;
using WireHQ.Application.Abstractions.Persistence;
using WireHQ.Application.Common.Messaging;
using WireHQ.Domain.Auditing;
using WireHQ.Modules.Orchestration.Authorization;
using WireHQ.Modules.Orchestration.Domain;
using WireHQ.Shared.Results;

namespace WireHQ.Modules.Orchestration.Application.Agents;

/// <summary>
/// Mints a single-use enrolment token for one agent. The CLEAR token is returned exactly once (it travels
/// in the install command, <c>wirehq-agent enroll --token …</c>); only its SHA-256 hash is stored, so it
/// can never be recovered from the database. The gateway burns it on first use. (ADR-028, docs/12 §8)
/// </summary>
public sealed record MintEnrollTokenCommand(int? TtlHours) : ICommand<MintEnrollTokenResponse>, IAuthorizedRequest
{
    public IReadOnlyCollection<string> RequiredPermissions => [OrchestrationPermissions.Agents.Manage];
}

public sealed record MintEnrollTokenResponse(Guid Id, string Token, DateTimeOffset ExpiresAtUtc);

public sealed class MintEnrollTokenCommandValidator : AbstractValidator<MintEnrollTokenCommand>
{
    public MintEnrollTokenCommandValidator() =>
        RuleFor(x => x.TtlHours).InclusiveBetween(1, 168).When(x => x.TtlHours is not null);
}

public sealed class MintEnrollTokenCommandHandler(
    IApplicationDbContext dbContext,
    ITenantContext tenant,
    IDateTimeProvider clock,
    IAuditWriter audit)
    : ICommandHandler<MintEnrollTokenCommand, MintEnrollTokenResponse>
{
    public Task<Result<MintEnrollTokenResponse>> Handle(MintEnrollTokenCommand command, CancellationToken cancellationToken)
    {
        if (tenant.OrganizationId is not { } organizationId)
        {
            return FromError(Error.Forbidden("auth.tenant_required", "No active organization."));
        }

        // 256 bits of entropy, hex-encoded — shell-safe in the install command and infeasible to guess.
        var clearToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var ttlHours = command.TtlHours ?? AgentEnrollmentToken.DefaultTtlHours;
        var expiresAtUtc = clock.UtcNow.AddHours(ttlHours);

        var result = AgentEnrollmentToken.Issue(
            Guid.CreateVersion7(), organizationId, AgentEnrollmentToken.HashToken(clearToken), expiresAtUtc);
        if (result.IsFailure)
        {
            return FromError(result.Error);
        }

        dbContext.Set<AgentEnrollmentToken>().Add(result.Value);
        audit.Record("orch.agent_enrollment_token.minted", AuditOutcome.Success,
            nameof(AgentEnrollmentToken), result.Value.Id.ToString(), new { expiresAtUtc });

        return Task.FromResult<Result<MintEnrollTokenResponse>>(
            new MintEnrollTokenResponse(result.Value.Id, clearToken, expiresAtUtc));
    }

    private static Task<Result<MintEnrollTokenResponse>> FromError(Error error) =>
        Task.FromResult<Result<MintEnrollTokenResponse>>(error);
}

/// <summary>
/// Mints a single-use <b>re-key</b> token for an existing agent: the host redeems it with the ordinary
/// <c>wirehq-agent enroll --token …</c>, and the gateway re-issues THIS agent's certificate instead of creating a new
/// agent — so its id, name and every instance binding survive. The recovery path for an agent whose certificate
/// expired, and the cut-off for a host that may be compromised (the old certificate stops working on redemption).
/// Same permission as enrolling or binding an agent, because a re-key token carries the same power: whoever redeems
/// it receives that agent's jobs. A revoked agent cannot be re-keyed. (docs/13 §6.2)
/// </summary>
public sealed record MintRekeyTokenCommand(Guid AgentId, int? TtlHours) : ICommand<MintEnrollTokenResponse>, IAuthorizedRequest
{
    public IReadOnlyCollection<string> RequiredPermissions => [OrchestrationPermissions.Agents.Manage];
}

public sealed class MintRekeyTokenCommandValidator : AbstractValidator<MintRekeyTokenCommand>
{
    public MintRekeyTokenCommandValidator() =>
        RuleFor(x => x.TtlHours).InclusiveBetween(1, 168).When(x => x.TtlHours is not null);
}

public sealed class MintRekeyTokenCommandHandler(
    IApplicationDbContext dbContext,
    ITenantContext tenant,
    IDateTimeProvider clock,
    IAuditWriter audit)
    : ICommandHandler<MintRekeyTokenCommand, MintEnrollTokenResponse>
{
    public async Task<Result<MintEnrollTokenResponse>> Handle(MintRekeyTokenCommand command, CancellationToken cancellationToken)
    {
        if (tenant.OrganizationId is not { } organizationId)
        {
            return Error.Forbidden("auth.tenant_required", "No active organization.");
        }

        // Tenant-scoped: another org's agent id is simply not found.
        var agent = await dbContext.Set<Agent>().FirstOrDefaultAsync(a => a.Id == command.AgentId, cancellationToken);
        if (agent is null)
        {
            return OrchestrationErrors.Agent.NotFound;
        }

        if (agent.Status == AgentStatus.Revoked)
        {
            return OrchestrationErrors.Agent.RekeyNotAllowed;
        }

        var clearToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var expiresAtUtc = clock.UtcNow.AddHours(command.TtlHours ?? AgentEnrollmentToken.DefaultTtlHours);

        var result = AgentEnrollmentToken.IssueForRekey(
            Guid.CreateVersion7(), organizationId, AgentEnrollmentToken.HashToken(clearToken), expiresAtUtc, agent.Id);
        if (result.IsFailure)
        {
            return result.Error;
        }

        dbContext.Set<AgentEnrollmentToken>().Add(result.Value);
        audit.Record("orch.agent_rekey_token.minted", AuditOutcome.Success,
            nameof(Agent), agent.Id.ToString(), new { tokenId = result.Value.Id, expiresAtUtc });

        return new MintEnrollTokenResponse(result.Value.Id, clearToken, expiresAtUtc);
    }
}
