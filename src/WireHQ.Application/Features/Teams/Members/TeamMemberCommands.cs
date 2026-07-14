using FluentValidation;
using Microsoft.EntityFrameworkCore;
using WireHQ.Application.Abstractions;
using WireHQ.Application.Abstractions.Persistence;
using WireHQ.Application.Authorization;
using WireHQ.Application.Common.Messaging;
using WireHQ.Application.Memberships;
using WireHQ.Domain.Auditing;
using WireHQ.Domain.Teams;
using WireHQ.Shared.Results;

namespace WireHQ.Application.Features.Teams.Members;

// Managing a team's membership. Tenant-scoped (Teams + Memberships query filters), audited.

/// <summary>
/// Adds a colleague to a team by email: invites them into the organization first if they aren't already a
/// member (creating their account + a membership with <paramref name="RoleId"/> and emailing an
/// accept-invite link), then places that membership on the team. So "add an email to the team" grants
/// access to the customer's account. Idempotent on the team membership. (docs/03-multi-tenancy.md)
/// </summary>
public sealed record AddTeamMemberCommand(Guid TeamId, string Email, string? Name, Guid? RoleId)
    : ICommand<AddTeamMemberResponse>, IAuthorizedRequest
{
    public IReadOnlyCollection<string> RequiredPermissions => [Permissions.Teams.Manage];
}

/// <summary><see cref="Outcome"/> mirrors <see cref="InviteOutcome"/> so the UI can say "Invited" vs "Added".</summary>
public sealed record AddTeamMemberResponse(Guid TeamId, Guid MembershipId, string Outcome);

public sealed class AddTeamMemberCommandValidator : AbstractValidator<AddTeamMemberCommand>
{
    public AddTeamMemberCommandValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress().MaximumLength(320);
    }
}

public sealed class AddTeamMemberCommandHandler(
    IApplicationDbContext dbContext,
    ITenantContext tenant,
    UserInvitationService invitations,
    IAuditWriter audit)
    : ICommandHandler<AddTeamMemberCommand, AddTeamMemberResponse>
{
    public async Task<Result<AddTeamMemberResponse>> Handle(AddTeamMemberCommand command, CancellationToken cancellationToken)
    {
        if (tenant.OrganizationId is not { } organizationId)
        {
            return Error.Forbidden("auth.tenant_required", "No active organization.");
        }

        // Tenant-filtered to the active org, so a team from another tenant simply isn't found.
        var team = await dbContext.Teams
            .Include(t => t.Members)
            .FirstOrDefaultAsync(t => t.Id == command.TeamId, cancellationToken);
        if (team is null)
        {
            return TeamErrors.NotFound;
        }

        // Invite into the org (or reuse an existing membership), then add that membership to the team.
        var invited = await invitations.InviteOrGetMembershipAsync(
            organizationId, command.Email, command.Name, command.RoleId is { } roleId ? [roleId] : null, cancellationToken);
        if (invited.IsFailure)
        {
            return invited.Error;
        }

        var membershipId = invited.Value.MembershipId;
        team.AddMember(membershipId);

        audit.Record("identity.teams.member_added", AuditOutcome.Success, nameof(Team), team.Id.ToString(),
            new { membershipId, invited.Value.Outcome });

        return new AddTeamMemberResponse(team.Id, membershipId, invited.Value.Outcome.ToString());
    }
}

/// <summary>Removes a member from a team. 404 if that member is not on the team.</summary>
public sealed record RemoveTeamMemberCommand(Guid TeamId, Guid MembershipId) : ICommand, IAuthorizedRequest
{
    public IReadOnlyCollection<string> RequiredPermissions => [Permissions.Teams.Manage];
}

public sealed class RemoveTeamMemberCommandHandler(IApplicationDbContext dbContext, IAuditWriter audit)
    : ICommandHandler<RemoveTeamMemberCommand>
{
    public async Task<Result> Handle(RemoveTeamMemberCommand command, CancellationToken cancellationToken)
    {
        var team = await dbContext.Teams
            .Include(t => t.Members)
            .FirstOrDefaultAsync(t => t.Id == command.TeamId, cancellationToken);
        if (team is null)
        {
            return TeamErrors.NotFound;
        }

        var result = team.RemoveMember(command.MembershipId);
        if (result.IsFailure)
        {
            return result.Error;
        }

        audit.Record("identity.teams.member_removed", AuditOutcome.Success, nameof(Team), team.Id.ToString(),
            new { command.MembershipId });

        return Result.Success();
    }
}
