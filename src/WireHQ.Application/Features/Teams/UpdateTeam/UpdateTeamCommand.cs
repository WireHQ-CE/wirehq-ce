using FluentValidation;
using Microsoft.EntityFrameworkCore;
using WireHQ.Application.Abstractions;
using WireHQ.Application.Abstractions.Persistence;
using WireHQ.Application.Authorization;
using WireHQ.Application.Common.Messaging;
using WireHQ.Domain.Auditing;
using WireHQ.Domain.Teams;
using WireHQ.Shared.Results;

namespace WireHQ.Application.Features.Teams.UpdateTeam;

/// <summary>Renames a team and/or updates its description. Tenant-scoped + audited.</summary>
public sealed record UpdateTeamCommand(Guid Id, string? Name, string? Description) : ICommand, IAuthorizedRequest
{
    public IReadOnlyCollection<string> RequiredPermissions => [Permissions.Teams.Manage];
}

public sealed class UpdateTeamCommandValidator : AbstractValidator<UpdateTeamCommand>
{
    public UpdateTeamCommandValidator()
    {
        RuleFor(x => x.Name).MaximumLength(Team.MaxNameLength).When(x => x.Name is not null);
        RuleFor(x => x.Description).MaximumLength(512).When(x => x.Description is not null);
    }
}

public sealed class UpdateTeamCommandHandler(IApplicationDbContext dbContext, IAuditWriter audit)
    : ICommandHandler<UpdateTeamCommand>
{
    public async Task<Result> Handle(UpdateTeamCommand command, CancellationToken cancellationToken)
    {
        var team = await dbContext.Teams.FirstOrDefaultAsync(t => t.Id == command.Id, cancellationToken);
        if (team is null)
        {
            return TeamErrors.NotFound;
        }

        if (command.Name is not null)
        {
            var rename = team.Rename(command.Name);
            if (rename.IsFailure)
            {
                return rename.Error;
            }
        }

        if (command.Description is not null)
        {
            team.Describe(command.Description);
        }

        audit.Record("identity.teams.update", AuditOutcome.Success, nameof(Team), team.Id.ToString(),
            new { command.Name, command.Description });

        return Result.Success();
    }
}
