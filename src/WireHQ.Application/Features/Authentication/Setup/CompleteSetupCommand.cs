using FluentValidation;
using Microsoft.EntityFrameworkCore;
using WireHQ.Application.Abstractions;
using WireHQ.Application.Abstractions.Persistence;
using WireHQ.Application.Abstractions.Security;
using WireHQ.Application.Common.Messaging;
using WireHQ.Application.Organizations;
using WireHQ.Domain.Auditing;
using WireHQ.Domain.Identity;
using WireHQ.Domain.Organizations;
using WireHQ.Domain.ValueObjects;
using WireHQ.Shared.Results;

namespace WireHQ.Application.Features.Authentication.Setup;

/// <summary>
/// The browser first-run setup: claims a fresh, ownerless instance by creating the first user and
/// their organization (through the same <see cref="OrganizationProvisioner"/> as signup, so the
/// Owner gets the full system-role catalog). Anonymous by necessity — there is nobody to sign in as
/// yet — and guarded twice: it must be explicitly enabled (<c>Setup:Enabled</c>, the self-host
/// posture) and it refuses the moment ANY user exists, so an established instance can never be
/// re-claimed. The email is marked verified: setup happens before SMTP is configured, so a
/// verification loop would be a dead end. (docs/17-community-edition.md)
/// </summary>
public sealed record CompleteSetupCommand(
    string Email,
    string FirstName,
    string LastName,
    string Password,
    string? OrganizationName) : ICommand<CompleteSetupResponse>, ITenantUnscopedRequest;

public sealed record CompleteSetupResponse(Guid UserId, Guid OrganizationId, string OrganizationSlug);

public static class SetupErrors
{
    public static readonly Error NotAvailable = Error.Forbidden(
        "setup.not_available",
        "First-run setup is not available on this instance.");
}

public sealed class CompleteSetupCommandValidator : AbstractValidator<CompleteSetupCommand>
{
    public CompleteSetupCommandValidator()
    {
        RuleFor(x => x.Email)
            .NotEmpty()
            .EmailAddress()
            .MaximumLength(320);

        RuleFor(x => x.FirstName)
            .NotEmpty().WithMessage("First name is required.")
            .MaximumLength(User.MaxNamePartLength);

        RuleFor(x => x.LastName)
            .NotEmpty().WithMessage("Last name is required.")
            .MaximumLength(User.MaxNamePartLength);

        // Optional — the instance is simply named "WireHQ" when omitted (the seeder's default too).
        RuleFor(x => x.OrganizationName)
            .MaximumLength(Organization.MaxNameLength)
            .When(x => !string.IsNullOrWhiteSpace(x.OrganizationName));

        // Same structural policy as registration.
        RuleFor(x => x.Password)
            .NotEmpty()
            .MinimumLength(12).WithMessage("Password must be at least 12 characters.")
            .MaximumLength(256)
            .Must(p => p.Any(char.IsUpper) && p.Any(char.IsLower) && p.Any(char.IsDigit))
            .WithMessage("Password must include upper- and lower-case letters and a number.");
    }
}

public sealed class CompleteSetupCommandHandler(
    IApplicationDbContext dbContext,
    IPasswordHasher passwordHasher,
    OrganizationProvisioner provisioner,
    IAuditWriter audit,
    SetupOptions setup)
    : ICommandHandler<CompleteSetupCommand, CompleteSetupResponse>
{
    public async Task<Result<CompleteSetupResponse>> Handle(CompleteSetupCommand request, CancellationToken cancellationToken)
    {
        if (!setup.Enabled)
        {
            return SetupErrors.NotAvailable;
        }

        // One-shot: any existing user (seeded, set up, or invited) means the instance is claimed.
        // Two simultaneous first submits race a millisecond window here; the loser fails on the
        // unique email index or simply co-exists — acceptable for a first-boot flow on the
        // operator's own host.
        var anyUsers = await dbContext.Users
            .IgnoreQueryFilters()
            .AnyAsync(cancellationToken);

        if (anyUsers)
        {
            return SetupErrors.NotAvailable;
        }

        var userResult = User.Register(request.Email, request.FirstName, request.LastName, passwordHasher.Hash(request.Password));
        if (userResult.IsFailure)
        {
            return userResult.Error;
        }

        var owner = userResult.Value;
        owner.VerifyEmail();
        dbContext.Users.Add(owner);

        var organizationName = string.IsNullOrWhiteSpace(request.OrganizationName)
            ? "WireHQ"
            : request.OrganizationName.Trim();
        var slugResult = Slug.FromName(organizationName);
        var slug = slugResult.IsSuccess ? slugResult.Value.Value : "wirehq";

        var provisioned = await provisioner.ProvisionAsync(organizationName, slug, owner.Id, cancellationToken);
        if (provisioned.IsFailure)
        {
            return provisioned.Error;
        }

        var organization = provisioned.Value.Organization;

        audit.Record(
            action: "auth.setup_completed",
            outcome: AuditOutcome.Success,
            targetType: nameof(User),
            targetId: owner.Id.ToString(),
            changes: new { owner.Email.Value, OrganizationSlug = organization.Slug.Value });

        return new CompleteSetupResponse(owner.Id, organization.Id, organization.Slug.Value);
    }
}
