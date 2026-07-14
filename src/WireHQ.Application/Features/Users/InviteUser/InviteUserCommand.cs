using FluentValidation;
using WireHQ.Application.Abstractions;
using WireHQ.Application.Authorization;
using WireHQ.Application.Common.Messaging;
using WireHQ.Application.Memberships;
using WireHQ.Domain.Identity;
using WireHQ.Domain.Memberships;
using WireHQ.Shared.Results;

namespace WireHQ.Application.Features.Users.InviteUser;

/// <summary>Invites a user to the active organization, creating a platform account if needed.</summary>
public sealed record InviteUserCommand(string Email, string? Name, IReadOnlyCollection<Guid>? RoleIds)
    : ICommand<InviteUserResponse>, IAuthorizedRequest, IRequiresVerifiedEmail
{
    public IReadOnlyCollection<string> RequiredPermissions => [Permissions.Users.Invite];
}

public sealed record InviteUserResponse(Guid UserId, Guid MembershipId);

public sealed class InviteUserCommandValidator : AbstractValidator<InviteUserCommand>
{
    public InviteUserCommandValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress().MaximumLength(320);
        RuleFor(x => x.Name).MaximumLength(User.MaxNameLength);
    }
}

public sealed class InviteUserCommandHandler(ITenantContext tenant, UserInvitationService invitations)
    : ICommandHandler<InviteUserCommand, InviteUserResponse>
{
    public async Task<Result<InviteUserResponse>> Handle(InviteUserCommand command, CancellationToken cancellationToken)
    {
        if (tenant.OrganizationId is not { } organizationId)
        {
            return Error.Forbidden("auth.tenant_required", "No active organization.");
        }

        var result = await invitations.InviteOrGetMembershipAsync(
            organizationId, command.Email, command.Name, command.RoleIds, cancellationToken);
        if (result.IsFailure)
        {
            return result.Error;
        }

        // The Users page treats re-inviting an existing member as an error (the Teams flow does not).
        if (result.Value.Outcome == InviteOutcome.AlreadyMember)
        {
            return MembershipErrors.AlreadyMember;
        }

        return new InviteUserResponse(result.Value.UserId, result.Value.MembershipId);
    }
}
