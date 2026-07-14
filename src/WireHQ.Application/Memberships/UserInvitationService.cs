using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WireHQ.Application.Abstractions;
using WireHQ.Application.Abstractions.Persistence;
using WireHQ.Application.Abstractions.Security;
using WireHQ.Application.Authorization;
using WireHQ.Application.Common.Email;
using WireHQ.Application.Entitlements;
using WireHQ.Domain.Auditing;
using WireHQ.Domain.Identity;
using WireHQ.Domain.Memberships;
using WireHQ.Domain.ValueObjects;
using WireHQ.Shared.Results;

namespace WireHQ.Application.Memberships;

/// <summary>What happened when resolving an invitee to a membership.</summary>
public enum InviteOutcome
{
    /// <summary>No platform account existed — one was created and a set-password invite emailed.</summary>
    InvitedNewUser,

    /// <summary>The person already had a WireHQ account; a membership was created + they were notified.</summary>
    AddedExistingUser,

    /// <summary>Already a member of this organization — the existing membership was returned, no email.</summary>
    AlreadyMember,
}

public sealed record InvitationResult(Guid UserId, Guid MembershipId, InviteOutcome Outcome);

/// <summary>
/// Shared "invite a colleague into an organization" logic, used by both the Users-page invite and the
/// Teams add-by-email flow. Like <see cref="WireHQ.Application.Organizations.OrganizationProvisioner"/>
/// it only adds entities to the unit of work — the calling command's UnitOfWork behavior commits them
/// (and the audit row) atomically. Email sending is inline + resilient: a mail failure never fails the
/// invite (the membership still lands). (docs/04-security.md)
/// </summary>
public sealed class UserInvitationService(
    IApplicationDbContext dbContext,
    IPasswordHasher passwordHasher,
    ITokenService tokenService,
    IDateTimeProvider clock,
    IEmailSender emailSender,
    IClientUrlBuilder urls,
    IEntitlementService entitlements,
    IAuditWriter audit,
    ILogger<UserInvitationService> logger)
{
    /// <summary>
    /// Resolves <paramref name="email"/> to a membership in <paramref name="organizationId"/>: reuses an
    /// existing membership, or creates the platform user (if new) + an invited membership with
    /// <paramref name="roleIds"/> (default: the Member system role) and emails an accept-invite link.
    /// </summary>
    public async Task<Result<InvitationResult>> InviteOrGetMembershipAsync(
        Guid organizationId,
        string email,
        string? name,
        IReadOnlyCollection<Guid>? roleIds,
        CancellationToken cancellationToken)
    {
        var emailResult = Email.Create(email);
        if (emailResult.IsFailure)
        {
            return emailResult.Error;
        }

        var address = emailResult.Value;

        // Reuse an existing platform account (global table), or create a placeholder one for the invitee.
        var user = await dbContext.Users
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.Email.Value == address.Value, cancellationToken);

        var isNewUser = user is null;
        if (user is null)
        {
            // Unusable random password until the invitee sets their own via the invite link.
            var placeholderHash = passwordHasher.Hash(Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"));
            var userResult = User.Register(address.Value, name ?? address.Value, placeholderHash);
            if (userResult.IsFailure)
            {
                return userResult.Error;
            }

            user = userResult.Value;
            dbContext.Users.Add(user);
        }

        // Already a member of THIS org? Memberships is tenant-filtered to the active org (== organizationId),
        // so this is org-scoped; return the existing membership and do nothing else.
        var existing = await dbContext.Memberships
            .FirstOrDefaultAsync(m => m.UserId == user.Id && !m.IsDeleted, cancellationToken);
        if (existing is not null)
        {
            return new InvitationResult(user.Id, existing.Id, InviteOutcome.AlreadyMember);
        }

        // Plan quota: a new membership consumes a seat (re-adding an existing member above doesn't).
        var seatCount = await dbContext.Memberships.CountAsync(m => !m.IsDeleted, cancellationToken);
        var withinSeats = await entitlements.EnsureCanAddAsync(PlanResource.Seats, seatCount, cancellationToken);
        if (withinSeats.IsFailure)
        {
            return withinSeats.Error;
        }

        var resolvedRoles = await ResolveRoleIdsAsync(roleIds, cancellationToken);
        var membership = Membership.Invite(organizationId, user.Id, resolvedRoles);
        dbContext.Memberships.Add(membership);

        audit.Record("identity.users.invite", AuditOutcome.Success, nameof(Membership), membership.Id.ToString(),
            new { email = address.Value, roleIds = resolvedRoles });

        await SendInviteEmailAsync(user.Id, address.Value, isNewUser, cancellationToken);

        return new InvitationResult(user.Id, membership.Id,
            isNewUser ? InviteOutcome.InvitedNewUser : InviteOutcome.AddedExistingUser);
    }

    private async Task SendInviteEmailAsync(Guid userId, string emailAddress, bool isNewUser, CancellationToken cancellationToken)
    {
        var orgName = await dbContext.Organizations
            .Select(o => o.Name)
            .FirstOrDefaultAsync(cancellationToken) ?? "your organization";

        try
        {
            if (isNewUser)
            {
                // A set-password link (a 7-day reset token) doubles as accept-invite: setting the password
                // verifies + activates the account (ResetPassword calls VerifyEmail).
                var raw = tokenService.IssueRefreshToken(clock.UtcNow.AddDays(7));
                dbContext.PasswordResetTokens.Add(PasswordResetToken.Issue(userId, raw.Hash, raw.ExpiresAtUtc));
                await emailSender.SendAsync(
                    EmailTemplates.Invite(emailAddress, orgName, urls.ResetPasswordUrl(raw.Value)),
                    cancellationToken);
            }
            else
            {
                // The invitee already has a WireHQ account — just notify them they've been added.
                await emailSender.SendAsync(
                    EmailTemplates.AddedToOrganization(emailAddress, orgName, urls.LoginUrl()),
                    cancellationToken);
            }
        }
        catch (Exception ex)
        {
            // The membership is created regardless; a mail failure must not fail the invite.
            logger.LogWarning(ex, "Failed to send invitation email to {Email}.", emailAddress);
        }
    }

    private async Task<IReadOnlyCollection<Guid>> ResolveRoleIdsAsync(
        IReadOnlyCollection<Guid>? requested, CancellationToken cancellationToken)
    {
        if (requested is { Count: > 0 })
        {
            // Only accept roles that actually belong to this org (Roles is tenant-filtered).
            return await dbContext.Roles
                .Where(r => requested.Contains(r.Id))
                .Select(r => r.Id)
                .ToListAsync(cancellationToken);
        }

        // Default to the Member system role.
        var memberRoleId = await dbContext.Roles
            .Where(r => r.Name == SystemRoles.Member)
            .Select(r => r.Id)
            .FirstOrDefaultAsync(cancellationToken);

        return memberRoleId == Guid.Empty ? [] : [memberRoleId];
    }
}
