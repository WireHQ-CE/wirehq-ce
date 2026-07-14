using WireHQ.Domain.Common;
using WireHQ.Domain.ValueObjects;
using WireHQ.Shared.Results;

namespace WireHQ.Domain.Organizations;

/// <summary>
/// The tenant aggregate root — the unit of isolation, billing, and data ownership
/// (see docs/03-multi-tenancy.md). Note it is <em>not</em> <see cref="ITenantOwned"/>: it
/// defines the tenant rather than belonging to one.
/// </summary>
public sealed class Organization : AggregateRoot, IAuditable, ISoftDeletable
{
    public const int MaxNameLength = 128;

    // EF Core
    private Organization()
    {
    }

    private Organization(Guid id, Slug slug, string name)
        : base(id)
    {
        Slug = slug;
        Name = name;
        Status = OrganizationStatus.Active;
        Edition = OrganizationEdition.Community;
    }

    public Slug Slug { get; private set; } = null!;

    public string Name { get; private set; } = null!;

    public OrganizationStatus Status { get; private set; }

    public OrganizationEdition Edition { get; private set; }

    /// <summary>Home region for data residency / routing. Null until placed (SaaS). </summary>
    public string? DataRegion { get; private set; }

    // Business profile — optional details a real customer fills in (Settings → Organization). Billing
    // address lives separately on BillingProfile.
    public string? LegalName { get; private set; }
    public string? Website { get; private set; }
    public string? Industry { get; private set; }
    public string? CompanySize { get; private set; }
    public string? Country { get; private set; }
    public string? Timezone { get; private set; }

    // IAuditable
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public Guid? CreatedBy { get; private set; }
    public DateTimeOffset? UpdatedAtUtc { get; private set; }
    public Guid? UpdatedBy { get; private set; }

    // ISoftDeletable
    public bool IsDeleted { get; private set; }
    public DateTimeOffset? DeletedAtUtc { get; private set; }
    public Guid? DeletedBy { get; private set; }

    public static Result<Organization> Create(string name, string slug)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > MaxNameLength)
        {
            return OrganizationErrors.InvalidName;
        }

        var slugResult = Slug.Create(slug);
        if (slugResult.IsFailure)
        {
            return slugResult.Error;
        }

        var organization = new Organization(Guid.CreateVersion7(), slugResult.Value, name.Trim());
        organization.Raise(new OrganizationCreated(organization.Id, organization.Slug.Value, organization.Name));
        return organization;
    }

    public Result Rename(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > MaxNameLength)
        {
            return OrganizationErrors.InvalidName;
        }

        Name = name.Trim();
        Raise(new OrganizationRenamed(Id, Name));
        return Result.Success();
    }

    /// <summary>
    /// Update the organization's name + optional business-profile fields (Settings → Organization).
    /// Blank optional values are normalised to null. Name follows the same rules as <see cref="Rename"/>.
    /// </summary>
    public Result UpdateProfile(
        string name, string? legalName, string? website, string? industry,
        string? companySize, string? country, string? timezone)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > MaxNameLength)
        {
            return OrganizationErrors.InvalidName;
        }

        var renamed = Name != name.Trim();
        Name = name.Trim();
        LegalName = Normalize(legalName);
        Website = Normalize(website);
        Industry = Normalize(industry);
        CompanySize = Normalize(companySize);
        Country = Normalize(country);
        Timezone = Normalize(timezone);

        if (renamed)
        {
            Raise(new OrganizationRenamed(Id, Name));
        }

        return Result.Success();
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public Result Suspend(string reason)
    {
        if (Status == OrganizationStatus.Suspended)
        {
            return OrganizationErrors.AlreadySuspended;
        }

        Status = OrganizationStatus.Suspended;
        Raise(new OrganizationSuspended(Id, reason));
        return Result.Success();
    }

    public Result Reactivate()
    {
        if (Status == OrganizationStatus.Active)
        {
            return Result.Success();
        }

        Status = OrganizationStatus.Active;
        Raise(new OrganizationReactivated(Id));
        return Result.Success();
    }

    public void SetEdition(OrganizationEdition edition) => Edition = edition;
}

public static class OrganizationErrors
{
    public static readonly Error InvalidName =
        Error.Validation("organization.invalid_name", "Organization name is required and must be 128 characters or fewer.");

    public static readonly Error AlreadySuspended =
        Error.Conflict("organization.already_suspended", "Organization is already suspended.");

    public static readonly Error SlugTaken =
        Error.Conflict("organization.slug_taken", "That organization URL is already in use.");

    public static readonly Error NotFound =
        Error.NotFound("organization.not_found", "Organization was not found.");
}
