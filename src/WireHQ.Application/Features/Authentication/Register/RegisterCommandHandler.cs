using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WireHQ.Application.Abstractions;
using WireHQ.Application.Abstractions.Persistence;
using WireHQ.Application.Abstractions.Security;
using WireHQ.Application.Common.Email;
using WireHQ.Application.Common.Messaging;
using WireHQ.Application.Organizations;
using WireHQ.Domain.Auditing;
using WireHQ.Domain.Identity;
using WireHQ.Domain.Onboarding;
using WireHQ.Domain.ValueObjects;
using WireHQ.Shared.Results;

namespace WireHQ.Application.Features.Authentication.Register;

public sealed class RegisterCommandHandler(
    IApplicationDbContext dbContext,
    IPasswordHasher passwordHasher,
    OrganizationProvisioner provisioner,
    ITokenService tokenService,
    IDateTimeProvider clock,
    IEmailSender emailSender,
    IClientUrlBuilder urls,
    IAuditWriter audit,
    RegistrationOptions registration,
    ILogger<RegisterCommandHandler> logger)
    : ICommandHandler<RegisterCommand, RegisterResponse>
{
    public async Task<Result<RegisterResponse>> Handle(RegisterCommand request, CancellationToken cancellationToken)
    {
        // Self-hosted installs run invite-only (Auth:OpenRegistration=false) — the UI hides signup too,
        // but the API is the enforcement point (docs/17-community-edition.md).
        if (!registration.OpenRegistration)
        {
            return UserErrors.RegistrationDisabled;
        }

        var emailResult = Email.Create(request.Email);
        if (emailResult.IsFailure)
        {
            return emailResult.Error;
        }

        var email = emailResult.Value;

        // Uniqueness is enforced by a DB unique index too; this gives a friendly error first.
        var emailTaken = await dbContext.Users
            .IgnoreQueryFilters()
            .AnyAsync(u => u.Email.Value == email.Value, cancellationToken);

        if (emailTaken)
        {
            return UserErrors.EmailTaken;
        }

        var passwordHash = passwordHasher.Hash(request.Password);

        var userResult = User.Register(request.Email, request.FirstName, request.LastName, passwordHash);
        if (userResult.IsFailure)
        {
            return userResult.Error;
        }

        var user = userResult.Value;
        user.AcceptTerms(clock.UtcNow);
        dbContext.Users.Add(user);

        // Business info is optional at signup — auto-name a personal workspace when none is given.
        var workspaceName = string.IsNullOrWhiteSpace(request.OrganizationName)
            ? $"{request.FirstName.Trim()}'s Workspace"
            : request.OrganizationName.Trim();

        // Derive a tenant slug from the workspace name, made unique — common first names (and a shared
        // demo DB) would otherwise collide on "{first}-s-workspace" and fail signup.
        var slugResult = Slug.FromName(workspaceName);
        var baseSlug = slugResult.IsSuccess ? slugResult.Value.Value : "workspace";
        var slug = await ResolveUniqueSlugAsync(baseSlug, cancellationToken);

        var provisionResult = await provisioner.ProvisionAsync(workspaceName, slug, user.Id, cancellationToken);
        if (provisionResult.IsFailure)
        {
            return provisionResult.Error;
        }

        var organization = provisionResult.Value.Organization;

        // Seed a pending onboarding profile so the Welcome Wizard is offered on first login (only
        // self-signup orgs get one — invited/platform-created orgs are not pushed through the wizard).
        dbContext.OnboardingProfiles.Add(OnboardingProfile.CreatePending(organization.Id));

        audit.Record(
            action: "auth.register",
            outcome: AuditOutcome.Success,
            targetType: nameof(User),
            targetId: user.Id.ToString(),
            changes: new { user.Email.Value, OrganizationSlug = organization.Slug.Value });

        // Send a "confirm your email" link (login is still allowed; the nudge prompts verification).
        var verification = tokenService.IssueRefreshToken(clock.UtcNow.AddDays(3));
        dbContext.EmailVerificationTokens.Add(EmailVerificationToken.Issue(user.Id, verification.Hash, verification.ExpiresAtUtc));
        try
        {
            await emailSender.SendAsync(
                EmailTemplates.VerifyEmail(user.Email.Value, user.Name, urls.VerifyEmailUrl(verification.Value)),
                cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to send verification email on registration.");
        }

        return new RegisterResponse(user.Id, organization.Id, organization.Slug.Value);
    }

    /// <summary>Returns the base slug if free, otherwise appends a short random suffix until one is — so a
    /// signup never fails just because another workspace already took the name.</summary>
    private async Task<string> ResolveUniqueSlugAsync(string baseSlug, CancellationToken cancellationToken)
    {
        var candidate = baseSlug;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var exists = await dbContext.Organizations
                .IgnoreQueryFilters()
                .AnyAsync(o => o.Slug.Value == candidate, cancellationToken);
            if (!exists)
            {
                return candidate;
            }

            var suffix = Guid.NewGuid().ToString("N")[..6];
            var trimmedBase = baseSlug.Length > Slug.MaxLength - 7 ? baseSlug[..(Slug.MaxLength - 7)] : baseSlug;
            candidate = $"{trimmedBase.TrimEnd('-')}-{suffix}";
        }

        return $"org-{Guid.NewGuid():N}"[..12];
    }
}
