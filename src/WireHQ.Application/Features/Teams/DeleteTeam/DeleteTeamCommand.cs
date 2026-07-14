using Microsoft.EntityFrameworkCore;
using WireHQ.Application.Abstractions;
using WireHQ.Application.Abstractions.Persistence;
using WireHQ.Application.Authorization;
using WireHQ.Application.Common.Messaging;
using WireHQ.Domain.Auditing;
using WireHQ.Domain.Teams;
using WireHQ.Shared.Results;

namespace WireHQ.Application.Features.Teams.DeleteTeam;

/// <summary>Soft-deletes a team (its member rows go with it via the soft-delete interceptor).</summary>
public sealed record DeleteTeamCommand(Guid Id) : ICommand, IAuthorizedRequest
{
    public IReadOnlyCollection<string> RequiredPermissions => [Permissions.Teams.Manage];
}

public sealed class DeleteTeamCommandHandler(IApplicationDbContext dbContext, IAuditWriter audit)
    : ICommandHandler<DeleteTeamCommand>
{
    public async Task<Result> Handle(DeleteTeamCommand command, CancellationToken cancellationToken)
    {
        // Load the join rows so the team's soft-delete also clears its membership associations.
        var team = await dbContext.Teams
            .Include(t => t.Members)
            .FirstOrDefaultAsync(t => t.Id == command.Id, cancellationToken);
        if (team is null)
        {
            return TeamErrors.NotFound;
        }

        dbContext.Teams.Remove(team); // soft-delete via the auditable interceptor

        audit.Record("identity.teams.delete", AuditOutcome.Success, nameof(Team), team.Id.ToString(),
            new { team.Name });

        return Result.Success();
    }
}
