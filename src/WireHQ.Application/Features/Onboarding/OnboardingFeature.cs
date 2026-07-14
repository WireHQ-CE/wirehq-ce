using FluentValidation;
using Microsoft.EntityFrameworkCore;
using WireHQ.Application.Abstractions;
using WireHQ.Application.Abstractions.Persistence;
using WireHQ.Application.Authorization;
using WireHQ.Application.Common.Messaging;
using WireHQ.Domain.Auditing;
using WireHQ.Domain.Onboarding;
using WireHQ.Shared.Results;

namespace WireHQ.Application.Features.Onboarding;

// The post-signup Welcome Wizard ("Tell us about your deployment"). Optional + skippable; data is stored
// per-org for Product/Sales segmentation. Save/Skip are gated on org.settings.update (the new owner holds
// it); they are NOT email-verify gated, so users can onboard before confirming their email.

public sealed record OnboardingResponse(
    string Status,
    bool ShouldShow,
    string? CompanyName,
    string? CompanyWebsite,
    string? Industry,
    string? TeamSize,
    string? VpnUsers,
    string? CurrentVpnSolution,
    string UseCase);

/// <summary>Returns the active org's onboarding state for the wizard (and whether to show it).</summary>
public sealed record GetOnboardingQuery : IQuery<OnboardingResponse>;

public sealed class GetOnboardingQueryHandler(IApplicationDbContext dbContext, ICurrentUser currentUser)
    : IQueryHandler<GetOnboardingQuery, OnboardingResponse>
{
    private static readonly Error NotAuthenticated = Error.Unauthorized("auth.unauthenticated", "Authentication is required.");

    public async Task<Result<OnboardingResponse>> Handle(GetOnboardingQuery query, CancellationToken cancellationToken)
    {
        if (!currentUser.IsAuthenticated)
        {
            return NotAuthenticated;
        }

        // Tenant-scoped by the global query filter to the active org.
        var profile = await dbContext.OnboardingProfiles.AsNoTracking().FirstOrDefaultAsync(cancellationToken);
        if (profile is null)
        {
            return new OnboardingResponse(nameof(OnboardingStatus.Skipped), false, null, null, null, null, null, null, nameof(OnboardingUseCase.Unspecified));
        }

        return new OnboardingResponse(
            profile.Status.ToString(),
            profile.IsPending,
            profile.CompanyName,
            profile.CompanyWebsite,
            profile.Industry,
            profile.TeamSize,
            profile.VpnUsers,
            profile.CurrentVpnSolution,
            profile.UseCase.ToString());
    }
}

/// <summary>Saves the wizard answers + marks it complete. A company name also renames the auto-workspace.</summary>
public sealed record SaveOnboardingCommand(
    string? CompanyName,
    string? CompanyWebsite,
    string? Industry,
    string? TeamSize,
    string? VpnUsers,
    string? CurrentVpnSolution,
    string? UseCase) : ICommand, IAuthorizedRequest
{
    public IReadOnlyCollection<string> RequiredPermissions => [Permissions.Organization.Update];
}

public sealed class SaveOnboardingCommandValidator : AbstractValidator<SaveOnboardingCommand>
{
    public SaveOnboardingCommandValidator()
    {
        RuleFor(x => x.CompanyName).MaximumLength(OnboardingProfile.MaxText);
        RuleFor(x => x.CompanyWebsite).MaximumLength(OnboardingProfile.MaxText);
        RuleFor(x => x.Industry).MaximumLength(OnboardingProfile.MaxText);
        RuleFor(x => x.TeamSize).MaximumLength(OnboardingProfile.MaxText);
        RuleFor(x => x.VpnUsers).MaximumLength(OnboardingProfile.MaxText);
        RuleFor(x => x.CurrentVpnSolution).MaximumLength(OnboardingProfile.MaxText);
    }
}

public sealed class SaveOnboardingCommandHandler(
    IApplicationDbContext dbContext,
    ITenantContext tenant,
    IDateTimeProvider clock,
    IAuditWriter audit)
    : ICommandHandler<SaveOnboardingCommand>
{
    private static readonly Error NoOrg = Error.Validation("onboarding.no_org", "No active organization.");

    public async Task<Result> Handle(SaveOnboardingCommand command, CancellationToken cancellationToken)
    {
        if (tenant.OrganizationId is not { } orgId)
        {
            return NoOrg;
        }

        var profile = await dbContext.OnboardingProfiles.FirstOrDefaultAsync(cancellationToken);
        if (profile is null)
        {
            profile = OnboardingProfile.CreatePending(orgId);
            dbContext.OnboardingProfiles.Add(profile);
        }

        var useCase = Enum.TryParse<OnboardingUseCase>(command.UseCase, ignoreCase: true, out var parsed)
            ? parsed
            : OnboardingUseCase.Unspecified;

        profile.Complete(
            command.CompanyName, command.CompanyWebsite, command.Industry,
            command.TeamSize, command.VpnUsers, command.CurrentVpnSolution, useCase, clock.UtcNow);

        // A real company name replaces the auto-generated "{First}'s Workspace".
        if (!string.IsNullOrWhiteSpace(command.CompanyName))
        {
            var organization = await dbContext.Organizations.FirstOrDefaultAsync(o => o.Id == orgId, cancellationToken);
            organization?.Rename(command.CompanyName.Trim());
        }

        audit.Record("onboarding.completed", AuditOutcome.Success, nameof(OnboardingProfile), profile.Id.ToString(),
            new { useCase = useCase.ToString(), command.TeamSize, command.CurrentVpnSolution });
        return Result.Success();
    }
}

/// <summary>Dismisses the wizard without answering.</summary>
public sealed record SkipOnboardingCommand : ICommand, IAuthorizedRequest
{
    public IReadOnlyCollection<string> RequiredPermissions => [Permissions.Organization.Update];
}

public sealed class SkipOnboardingCommandHandler(
    IApplicationDbContext dbContext,
    ITenantContext tenant,
    IDateTimeProvider clock,
    IAuditWriter audit)
    : ICommandHandler<SkipOnboardingCommand>
{
    private static readonly Error NoOrg = Error.Validation("onboarding.no_org", "No active organization.");

    public async Task<Result> Handle(SkipOnboardingCommand command, CancellationToken cancellationToken)
    {
        if (tenant.OrganizationId is not { } orgId)
        {
            return NoOrg;
        }

        var profile = await dbContext.OnboardingProfiles.FirstOrDefaultAsync(cancellationToken);
        if (profile is null)
        {
            profile = OnboardingProfile.CreatePending(orgId);
            dbContext.OnboardingProfiles.Add(profile);
        }

        profile.Skip(clock.UtcNow);
        audit.Record("onboarding.skipped", AuditOutcome.Success, nameof(OnboardingProfile), profile.Id.ToString());
        return Result.Success();
    }
}
