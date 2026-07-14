using FluentValidation;
using Microsoft.EntityFrameworkCore;
using WireHQ.Application.Abstractions;
using WireHQ.Application.Abstractions.Persistence;
using WireHQ.Application.Authorization;
using WireHQ.Application.Common.Messaging;
using WireHQ.Domain.Auditing;
using WireHQ.Domain.Organizations;
using WireHQ.Shared.Results;

namespace WireHQ.Application.Features.Organizations.UpdateOrganization;

/// <summary>Updates the active organization's name + optional business-profile fields. Owner/Admin only.</summary>
public sealed record UpdateOrganizationCommand(
    string Name,
    string? LegalName,
    string? Website,
    string? Industry,
    string? CompanySize,
    string? Country,
    string? Timezone) : ICommand, IAuthorizedRequest
{
    public IReadOnlyCollection<string> RequiredPermissions => [Permissions.Organization.Update];
}

public sealed class UpdateOrganizationCommandValidator : AbstractValidator<UpdateOrganizationCommand>
{
    public UpdateOrganizationCommandValidator()
    {
        RuleFor(c => c.Name).NotEmpty().MaximumLength(Organization.MaxNameLength);
        RuleFor(c => c.LegalName).MaximumLength(200);
        RuleFor(c => c.Website).MaximumLength(256);
        RuleFor(c => c.Industry).MaximumLength(100);
        RuleFor(c => c.CompanySize).MaximumLength(32);
        RuleFor(c => c.Country).MaximumLength(64);
        RuleFor(c => c.Timezone).MaximumLength(64);
    }
}

public sealed class UpdateOrganizationCommandHandler(
    IApplicationDbContext dbContext,
    ITenantContext tenant,
    IAuditWriter audit)
    : ICommandHandler<UpdateOrganizationCommand>
{
    public async Task<Result> Handle(UpdateOrganizationCommand command, CancellationToken cancellationToken)
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

        var result = organization.UpdateProfile(
            command.Name, command.LegalName, command.Website, command.Industry,
            command.CompanySize, command.Country, command.Timezone);

        if (result.IsFailure)
        {
            return result.Error;
        }

        audit.Record("organization.updated", AuditOutcome.Success, nameof(Organization), organizationId.ToString());
        return Result.Success();
    }
}
