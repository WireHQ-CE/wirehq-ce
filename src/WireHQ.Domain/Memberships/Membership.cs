using WireHQ.Domain.Common;
using WireHQ.Shared.Results;

namespace WireHQ.Domain.Memberships;

public enum MembershipStatus
{
    Invited = 0,
    Active = 1,
    Suspended = 2,
}

public sealed record MembershipInvited(Guid MembershipId, Guid OrganizationId, Guid UserId) : IDomainEvent;

public sealed record MembershipActivated(Guid MembershipId, Guid OrganizationId, Guid UserId) : IDomainEvent;

public sealed record MembershipRevoked(Guid MembershipId, Guid OrganizationId, Guid UserId) : IDomainEvent;

/// <summary>
/// A user's belonging to an organization, with the roles they hold there. This is the join of
/// the platform-global <see cref="Identity.User"/> into a tenant, and the anchor every
/// org-scoped permission check resolves against. A user has one membership per org.
/// </summary>
public sealed class Membership : AggregateRoot, ITenantOwned, IAuditable, ISoftDeletable
{
    private readonly List<MembershipRole> _roles = [];

    // EF Core
    private Membership()
    {
    }

    private Membership(Guid id, Guid organizationId, Guid userId, MembershipStatus status)
        : base(id)
    {
        OrganizationId = organizationId;
        UserId = userId;
        Status = status;
    }

    public Guid OrganizationId { get; private set; }

    public Guid UserId { get; private set; }

    public MembershipStatus Status { get; private set; }

    public DateTimeOffset? JoinedAtUtc { get; private set; }

    public IReadOnlyCollection<MembershipRole> Roles => _roles.AsReadOnly();

    public DateTimeOffset CreatedAtUtc { get; private set; }
    public Guid? CreatedBy { get; private set; }
    public DateTimeOffset? UpdatedAtUtc { get; private set; }
    public Guid? UpdatedBy { get; private set; }

    public bool IsDeleted { get; private set; }
    public DateTimeOffset? DeletedAtUtc { get; private set; }
    public Guid? DeletedBy { get; private set; }

    /// <summary>The owner membership created when an org is founded — active immediately.</summary>
    public static Membership CreateOwner(Guid organizationId, Guid userId, Guid ownerRoleId)
    {
        var membership = new Membership(Guid.CreateVersion7(), organizationId, userId, MembershipStatus.Active)
        {
            JoinedAtUtc = DateTimeOffset.UtcNow,
        };
        membership.AssignRole(ownerRoleId);
        membership.Raise(new MembershipActivated(membership.Id, organizationId, userId));
        return membership;
    }

    /// <summary>
    /// A membership that is <b>active immediately</b> with the given roles — no invite step. Used when a
    /// trusted flow adds a member directly (e.g. SSO just-in-time provisioning, docs/21 §7): the IdP has
    /// vouched for the user, so there is no invitation email or pending state.
    /// </summary>
    public static Membership CreateActive(Guid organizationId, Guid userId, IEnumerable<Guid> roleIds)
    {
        var membership = new Membership(Guid.CreateVersion7(), organizationId, userId, MembershipStatus.Active)
        {
            JoinedAtUtc = DateTimeOffset.UtcNow,
        };
        foreach (var roleId in roleIds)
        {
            membership.AssignRole(roleId);
        }

        membership.Raise(new MembershipActivated(membership.Id, organizationId, userId));
        return membership;
    }

    public static Membership Invite(Guid organizationId, Guid userId, IEnumerable<Guid> roleIds)
    {
        var membership = new Membership(Guid.CreateVersion7(), organizationId, userId, MembershipStatus.Invited);
        foreach (var roleId in roleIds)
        {
            membership.AssignRole(roleId);
        }

        membership.Raise(new MembershipInvited(membership.Id, organizationId, userId));
        return membership;
    }

    public Result Activate()
    {
        if (Status == MembershipStatus.Active)
        {
            return Result.Success();
        }

        Status = MembershipStatus.Active;
        JoinedAtUtc ??= DateTimeOffset.UtcNow;
        Raise(new MembershipActivated(Id, OrganizationId, UserId));
        return Result.Success();
    }

    public void Suspend() => Status = MembershipStatus.Suspended;

    public void Revoke() => Raise(new MembershipRevoked(Id, OrganizationId, UserId));

    public void AssignRole(Guid roleId)
    {
        if (_roles.All(r => r.RoleId != roleId))
        {
            _roles.Add(new MembershipRole(Id, roleId));
        }
    }

    public void RemoveRole(Guid roleId) => _roles.RemoveAll(r => r.RoleId == roleId);
}

/// <summary>Join entity: a role held by a membership (a user's roles within one org).</summary>
public sealed class MembershipRole
{
    // EF Core
    private MembershipRole()
    {
    }

    public MembershipRole(Guid membershipId, Guid roleId)
    {
        MembershipId = membershipId;
        RoleId = roleId;
    }

    public Guid MembershipId { get; private set; }

    public Guid RoleId { get; private set; }
}

public static class MembershipErrors
{
    public static readonly Error AlreadyMember = Error.Conflict("membership.already_member", "That user is already a member of this organization.");
    public static readonly Error NotFound = Error.NotFound("membership.not_found", "Membership was not found.");
    public static readonly Error LastOwner = Error.Conflict("membership.last_owner", "You cannot remove the last owner of an organization.");
}
