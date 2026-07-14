using System.Security.Cryptography;
using FluentValidation;
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
