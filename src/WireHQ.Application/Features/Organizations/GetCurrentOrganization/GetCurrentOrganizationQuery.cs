using Microsoft.EntityFrameworkCore;
using WireHQ.Application.Abstractions;
using WireHQ.Application.Abstractions.Persistence;
using WireHQ.Application.Authorization;
using WireHQ.Application.Common.Messaging;
using WireHQ.Domain.Organizations;
using WireHQ.Shared.Results;

namespace WireHQ.Application.Features.Organizations.GetCurrentOrganization;

public sealed record GetCurrentOrganizationQuery : IQuery<OrganizationResponse>, IAuthorizedRequest
{
    public IReadOnlyCollection<string> RequiredPermissions => [Permissions.Organization.Read];
}

public sealed record OrganizationResponse(
    Guid Id,
    string Slug,
    string Name,
    string Status,
    string Edition,
    string? LegalName,
    string? Website,
    string? Industry,
    string? CompanySize,
    string? Country,
    string? Timezone,
    int MemberCount,
    int TeamCount,
    DateTimeOffset CreatedAtUtc);

public sealed class GetCurrentOrganizationQueryHandler(IApplicationDbContext dbContext, ITenantContext tenant)
    : IQueryHandler<GetCurrentOrganizationQuery, OrganizationResponse>
{
    public async Task<Result<OrganizationResponse>> Handle(GetCurrentOrganizationQuery query, CancellationToken cancellationToken)
    {
        if (tenant.OrganizationId is not { } organizationId)
        {
            return OrganizationErrors.NotFound;
        }

        var organization = await dbContext.Organizations
            .FirstOrDefaultAsync(o => o.Id == organizationId, cancellationToken);

        if (organization is null)
        {
            return OrganizationErrors.NotFound;
        }

        var memberCount = await dbContext.Memberships.CountAsync(m => !m.IsDeleted, cancellationToken);
        var teamCount = await dbContext.Teams.CountAsync(t => !t.IsDeleted, cancellationToken);

        return new OrganizationResponse(
            organization.Id,
            organization.Slug.Value,
            organization.Name,
            organization.Status.ToString(),
            organization.Edition.ToString(),
            organization.LegalName,
            organization.Website,
            organization.Industry,
            organization.CompanySize,
            organization.Country,
            organization.Timezone,
            memberCount,
            teamCount,
            organization.CreatedAtUtc);
    }
}
