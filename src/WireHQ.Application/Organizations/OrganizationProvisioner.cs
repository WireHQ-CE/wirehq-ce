using Microsoft.EntityFrameworkCore;
using WireHQ.Application.Abstractions.Persistence;
using WireHQ.Application.Authorization;
using WireHQ.Application.Entitlements;
using WireHQ.Domain.Memberships;
using WireHQ.Domain.Organizations;
using WireHQ.Domain.Authorization;
using WireHQ.Shared.Results;

namespace WireHQ.Application.Organizations;

/// <summary>
/// Stands up a fully-formed tenant: the <see cref="Organization"/>, its seeded system roles
/// (Owner/Admin/Member/Billing/Auditor) wired to the permission catalog, default settings, and
/// the founder's owner <see cref="Membership"/>. Entities are added to the unit of work but NOT
/// saved here — the UnitOfWork behavior commits them atomically with the calling command.
/// </summary>
public sealed class OrganizationProvisioner(IApplicationDbContext dbContext, EntitlementOptions entitlementOptions)
{
    public async Task<Result<ProvisionedOrganization>> ProvisionAsync(
        string organizationName,
        string slug,
        Guid ownerUserId,
        CancellationToken cancellationToken)
    {
        var slugExists = await dbContext.Organizations
            .IgnoreQueryFilters()
            .AnyAsync(o => o.Slug.Value == slug, cancellationToken);

        if (slugExists)
        {
            return OrganizationErrors.SlugTaken;
        }

        var organizationResult = Organization.Create(organizationName, slug);
        if (organizationResult.IsFailure)
        {
            return organizationResult.Error;
        }

        var organization = organizationResult.Value;
        organization.SetEdition(entitlementOptions.DefaultEdition);
        dbContext.Organizations.Add(organization);
        dbContext.OrganizationSettings.Add(OrganizationSettings.CreateDefault(organization.Id));

        // Resolve the permission catalog (key → id) once, then seed the system roles.
        var permissionIds = await dbContext.Permissions
            .ToDictionaryAsync(p => p.Key, p => p.Id, cancellationToken);

        Role? ownerRole = null;
        foreach (var (roleName, permissionKeys) in SystemRoles.Definitions)
        {
            var roleResult = Role.Create(organization.Id, roleName, isSystem: true);
            if (roleResult.IsFailure)
            {
                return roleResult.Error;
            }

            var role = roleResult.Value;

            // The Owner always holds every permission in the catalog — including module
            // permissions (e.g. wg.*) contributed beyond the core SystemRoles definitions.
            IEnumerable<string> keysToGrant = roleName == SystemRoles.Owner ? permissionIds.Keys : permissionKeys;
            foreach (var key in keysToGrant)
            {
                if (permissionIds.TryGetValue(key, out var permissionId))
                {
                    role.Grant(permissionId);
                }
            }

            dbContext.Roles.Add(role);
            if (roleName == SystemRoles.Owner)
            {
                ownerRole = role;
            }
        }

        if (ownerRole is null)
        {
            return Error.Failure("organization.provisioning_failed", "Owner role could not be created.");
        }

        var ownerMembership = Membership.CreateOwner(organization.Id, ownerUserId, ownerRole.Id);
        dbContext.Memberships.Add(ownerMembership);

        return new ProvisionedOrganization(organization, ownerMembership);
    }
}

public sealed record ProvisionedOrganization(Organization Organization, Membership OwnerMembership);
