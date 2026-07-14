using FluentValidation;
using WireHQ.Application.Abstractions.Persistence;
using WireHQ.Application.Abstractions.Security;
using WireHQ.Application.Common.Messaging;
using WireHQ.Domain.Auditing;
using WireHQ.Application.Abstractions;
using WireHQ.Modules.Orchestration.Authorization;
using WireHQ.Modules.Orchestration.Domain;
using WireHQ.Shared.Results;

namespace WireHQ.Modules.Orchestration.Application.SshTargets;

/// <summary>
/// Registers an SSH deployment target. The credential (private key PEM or password) is encrypted at
/// rest immediately via <see cref="ISecretProtector"/> and never returned on read.
/// (docs/12-remote-orchestration.md §6)
/// </summary>
public sealed record CreateSshTargetCommand(
    string Name,
    string Host,
    int? Port,
    string Username,
    string AuthKind,
    string Credential,
    string? HostKeyFingerprint) : ICommand<CreateSshTargetResponse>, IAuthorizedRequest
{
    public IReadOnlyCollection<string> RequiredPermissions => [OrchestrationPermissions.Targets.Manage];
}

public sealed record CreateSshTargetResponse(Guid Id);

public sealed class CreateSshTargetCommandValidator : AbstractValidator<CreateSshTargetCommand>
{
    public CreateSshTargetCommandValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(SshTarget.MaxNameLength);
        RuleFor(x => x.Host).NotEmpty();
        RuleFor(x => x.Username).NotEmpty();
        RuleFor(x => x.Credential).NotEmpty();
        RuleFor(x => x.Port).InclusiveBetween(1, 65535).When(x => x.Port is not null);
        RuleFor(x => x.AuthKind).Must(a => Enum.TryParse<SshAuthKind>(a, ignoreCase: true, out _))
            .WithMessage("AuthKind must be 'PrivateKey' or 'Password'.");
    }
}

public sealed class CreateSshTargetCommandHandler(
    IApplicationDbContext dbContext,
    ITenantContext tenant,
    ISecretProtector secretProtector,
    IAuditWriter audit)
    : ICommandHandler<CreateSshTargetCommand, CreateSshTargetResponse>
{
    public Task<Result<CreateSshTargetResponse>> Handle(CreateSshTargetCommand command, CancellationToken cancellationToken)
    {
        if (tenant.OrganizationId is not { } organizationId)
        {
            return FromError(Error.Forbidden("auth.tenant_required", "No active organization."));
        }

        var authKind = Enum.Parse<SshAuthKind>(command.AuthKind, ignoreCase: true);
        var ciphertext = secretProtector.Protect(command.Credential);

        var result = SshTarget.Create(
            Guid.CreateVersion7(), organizationId, command.Name, command.Host, command.Port, command.Username,
            authKind, ciphertext, command.HostKeyFingerprint);
        if (result.IsFailure)
        {
            return FromError(result.Error);
        }

        var target = result.Value;
        dbContext.Set<SshTarget>().Add(target);

        audit.Record("orch.ssh_target.created", AuditOutcome.Success, nameof(SshTarget), target.Id.ToString(),
            new { target.Name, target.Host, target.Port, target.Username, authKind = authKind.ToString() });

        return Task.FromResult<Result<CreateSshTargetResponse>>(new CreateSshTargetResponse(target.Id));
    }

    private static Task<Result<CreateSshTargetResponse>> FromError(Error error) =>
        Task.FromResult<Result<CreateSshTargetResponse>>(error);
}
