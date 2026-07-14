using FluentValidation;
using Microsoft.EntityFrameworkCore;
using WireHQ.Application.Abstractions;
using WireHQ.Application.Abstractions.Persistence;
using WireHQ.Application.Authorization;
using WireHQ.Application.Common.Messaging;
using WireHQ.Application.Entitlements;
using WireHQ.Domain.Auditing;
using WireHQ.Domain.Teams;
using WireHQ.Shared.Results;

namespace WireHQ.Application.Features.Teams.CreateTeam;

/// <summary>Creates a team in the active organization. Teams are a Pro+ feature.</summary>
public sealed record CreateTeamCommand(string Name, string? Description)
    : ICommand<CreateTeamResponse>, IAuthorizedRequest, IRequiresVerifiedEmail, IRequiresFeature
{
    public IReadOnlyCollection<string> RequiredPermissions => [Permissions.Teams.Manage];

    public string RequiredFeature => PlanFeatures.Teams;
}

public sealed record CreateTeamResponse(Guid Id);

public sealed class CreateTeamCommandValidator : AbstractValidator<CreateTeamCommand>
{
    public CreateTeamCommandValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(Team.MaxNameLength);
        RuleFor(x => x.Description).MaximumLength(512);
    }
}

public sealed class CreateTeamCommandHandler(
    IApplicationDbContext dbContext,
    ITenantContext tenant,
    IAuditWriter audit)
    : ICommandHandler<CreateTeamCommand, CreateTeamResponse>
{
    public async Task<Result<CreateTeamResponse>> Handle(CreateTeamCommand command, CancellationToken cancellationToken)
    {
        if (tenant.OrganizationId is not { } organizationId)
        {
            return Error.Forbidden("auth.tenant_required", "No active organization.");
        }

        var teamResult = Team.Create(organizationId, command.Name);
        if (teamResult.IsFailure)
        {
            return teamResult.Error;
        }

        var team = teamResult.Value;

        // Slug uniqueness within the org (the tenant filter already scopes + hides deleted rows),
        // the same in-code guard OrganizationProvisioner uses for the org slug.
        var slugTaken = await dbContext.Teams.AnyAsync(t => t.Slug.Value == team.Slug.Value, cancellationToken);
        if (slugTaken)
        {
            return TeamErrors.SlugTaken;
        }

        team.Describe(command.Description);
        dbContext.Teams.Add(team);

        audit.Record("identity.teams.create", AuditOutcome.Success, nameof(Team), team.Id.ToString(),
            new { team.Name, Slug = team.Slug.Value });

        return new CreateTeamResponse(team.Id);
    }
}
