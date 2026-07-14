using FluentValidation;
using Microsoft.EntityFrameworkCore;
using WireHQ.Application.Abstractions;
using WireHQ.Application.Abstractions.Persistence;
using WireHQ.Application.Abstractions.Security;
using WireHQ.Application.Common.Messaging;
using WireHQ.Domain.Auditing;
using WireHQ.Modules.Orchestration.Authorization;
using WireHQ.Modules.Orchestration.Domain;
using WireHQ.Shared.Results;

namespace WireHQ.Modules.Orchestration.Application.SshTargets;

/// <summary>Updates an SSH target's name/connection/host-key, and optionally rotates its credential.</summary>
public sealed record UpdateSshTargetCommand(
    Guid Id,
    string? Name,
    string? Host,
    int? Port,
    string? Username,
    string? HostKeyFingerprint,
    string? AuthKind,
    string? Credential) : ICommand, IAuthorizedRequest
{
    public IReadOnlyCollection<string> RequiredPermissions => [OrchestrationPermissions.Targets.Manage];
}

public sealed class UpdateSshTargetCommandValidator : AbstractValidator<UpdateSshTargetCommand>
{
    public UpdateSshTargetCommandValidator()
    {
        RuleFor(x => x.Name).MaximumLength(SshTarget.MaxNameLength).When(x => x.Name is not null);
        RuleFor(x => x.Port).InclusiveBetween(1, 65535).When(x => x.Port is not null);
        RuleFor(x => x.AuthKind).Must(a => Enum.TryParse<SshAuthKind>(a, ignoreCase: true, out _))
            .When(x => !string.IsNullOrEmpty(x.Credential) && x.AuthKind is not null)
            .WithMessage("AuthKind must be 'PrivateKey' or 'Password'.");
    }
}

public sealed class UpdateSshTargetCommandHandler(
    IApplicationDbContext dbContext,
    ISecretProtector secretProtector,
    IAuditWriter audit)
    : ICommandHandler<UpdateSshTargetCommand>
{
    public async Task<Result> Handle(UpdateSshTargetCommand command, CancellationToken cancellationToken)
    {
        var target = await dbContext.Set<SshTarget>().FirstOrDefaultAsync(t => t.Id == command.Id, cancellationToken);
        if (target is null)
        {
            return OrchestrationErrors.SshTarget.NotFound;
        }

        if (command.Name is { } name)
        {
            var rename = target.Rename(name);
            if (rename.IsFailure)
            {
                return rename.Error;
            }
        }

        var connection = target.UpdateConnection(command.Host ?? target.Host, command.Port, command.Username ?? target.Username);
        if (connection.IsFailure)
        {
            return connection.Error;
        }

        if (command.HostKeyFingerprint is not null)
        {
            target.SetHostKeyFingerprint(command.HostKeyFingerprint);
        }

        if (!string.IsNullOrEmpty(command.Credential))
        {
            var authKind = command.AuthKind is { } a ? Enum.Parse<SshAuthKind>(a, ignoreCase: true) : target.AuthKind;
            target.RotateCredential(authKind, secretProtector.Protect(command.Credential));
        }

        audit.Record("orch.ssh_target.updated", AuditOutcome.Success, nameof(SshTarget), target.Id.ToString(),
            new { credentialRotated = !string.IsNullOrEmpty(command.Credential) });

        return Result.Success();
    }
}

/// <summary>Soft-deletes an SSH target.</summary>
public sealed record DeleteSshTargetCommand(Guid Id) : ICommand, IAuthorizedRequest
{
    public IReadOnlyCollection<string> RequiredPermissions => [OrchestrationPermissions.Targets.Manage];
}

public sealed class DeleteSshTargetCommandHandler(IApplicationDbContext dbContext, IAuditWriter audit)
    : ICommandHandler<DeleteSshTargetCommand>
{
    public async Task<Result> Handle(DeleteSshTargetCommand command, CancellationToken cancellationToken)
    {
        var target = await dbContext.Set<SshTarget>().FirstOrDefaultAsync(t => t.Id == command.Id, cancellationToken);
        if (target is null)
        {
            return OrchestrationErrors.SshTarget.NotFound;
        }

        dbContext.Set<SshTarget>().Remove(target); // soft-delete via the auditable interceptor
        audit.Record("orch.ssh_target.deleted", AuditOutcome.Success, nameof(SshTarget), target.Id.ToString());

        return Result.Success();
    }
}
