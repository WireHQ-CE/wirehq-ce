using WireHQ.Domain.Common;
using WireHQ.Domain.ValueObjects;
using WireHQ.Shared.Results;

namespace WireHQ.Domain.Teams;

/// <summary>
/// An intra-tenant grouping used to scope permissions and own resources. A team is
/// <em>not</em> an isolation boundary — teammates share the org's data. (docs/03-multi-tenancy.md)
/// </summary>
public sealed class Team : AggregateRoot, ITenantOwned, IAuditable, ISoftDeletable
{
    public const int MaxNameLength = 96;

    private readonly List<TeamMember> _members = [];

    // EF Core
    private Team()
    {
    }

    private Team(Guid id, Guid organizationId, string name, Slug slug)
        : base(id)
    {
        OrganizationId = organizationId;
        Name = name;
        Slug = slug;
    }

    public Guid OrganizationId { get; private set; }

    public string Name { get; private set; } = null!;

    public Slug Slug { get; private set; } = null!;

    public string? Description { get; private set; }

    /// <summary>The org memberships that belong to this team, owned by the team aggregate.</summary>
    public IReadOnlyCollection<TeamMember> Members => _members.AsReadOnly();

    public DateTimeOffset CreatedAtUtc { get; private set; }
    public Guid? CreatedBy { get; private set; }
    public DateTimeOffset? UpdatedAtUtc { get; private set; }
    public Guid? UpdatedBy { get; private set; }

    public bool IsDeleted { get; private set; }
    public DateTimeOffset? DeletedAtUtc { get; private set; }
    public Guid? DeletedBy { get; private set; }

    public static Result<Team> Create(Guid organizationId, string name, string? slug = null)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > MaxNameLength)
        {
            return TeamErrors.InvalidName;
        }

        var slugResult = string.IsNullOrWhiteSpace(slug) ? Slug.FromName(name) : Slug.Create(slug);
        if (slugResult.IsFailure)
        {
            return slugResult.Error;
        }

        return new Team(Guid.CreateVersion7(), organizationId, name.Trim(), slugResult.Value);
    }

    public Result Rename(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > MaxNameLength)
        {
            return TeamErrors.InvalidName;
        }

        Name = name.Trim();
        return Result.Success();
    }

    public void Describe(string? description) => Description = description?.Trim();

    /// <summary>Adds an org membership to the team. Idempotent — re-adding the same member is a no-op.</summary>
    public void AddMember(Guid membershipId)
    {
        if (_members.All(m => m.MembershipId != membershipId))
        {
            _members.Add(new TeamMember(Id, membershipId));
        }
    }

    /// <summary>Removes a member from the team. Fails with <see cref="TeamErrors.MemberNotFound"/> if absent.</summary>
    public Result RemoveMember(Guid membershipId)
    {
        var removed = _members.RemoveAll(m => m.MembershipId == membershipId);
        return removed == 0 ? TeamErrors.MemberNotFound : Result.Success();
    }
}

/// <summary>Join entity: an org <see cref="Memberships.Membership"/> that belongs to a team.</summary>
public sealed class TeamMember
{
    // EF Core
    private TeamMember()
    {
    }

    public TeamMember(Guid teamId, Guid membershipId)
    {
        TeamId = teamId;
        MembershipId = membershipId;
        AddedAtUtc = DateTimeOffset.UtcNow;
    }

    public Guid TeamId { get; private set; }

    public Guid MembershipId { get; private set; }

    public DateTimeOffset AddedAtUtc { get; private set; }
}

public static class TeamErrors
{
    public static readonly Error InvalidName = Error.Validation("team.invalid_name", "Team name is required and must be 96 characters or fewer.");
    public static readonly Error SlugTaken = Error.Conflict("team.slug_taken", "A team with that URL already exists.");
    public static readonly Error NotFound = Error.NotFound("team.not_found", "Team was not found.");
    public static readonly Error MemberNotFound = Error.NotFound("team.member_not_found", "That member is not part of this team.");
}
