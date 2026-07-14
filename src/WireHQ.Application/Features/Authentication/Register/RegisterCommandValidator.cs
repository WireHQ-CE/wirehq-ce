using FluentValidation;
using WireHQ.Domain.Identity;
using WireHQ.Domain.Organizations;

namespace WireHQ.Application.Features.Authentication.Register;

public sealed class RegisterCommandValidator : AbstractValidator<RegisterCommand>
{
    public RegisterCommandValidator()
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

        RuleFor(x => x.AcceptTerms)
            .Equal(true).WithMessage("You must accept the Terms of Service to continue.");

        // Optional — a personal workspace is auto-created when omitted.
        RuleFor(x => x.OrganizationName)
            .MaximumLength(Organization.MaxNameLength)
            .When(x => !string.IsNullOrWhiteSpace(x.OrganizationName));

        // Structural password policy; strength/breach checks are layered on in Identity.
        RuleFor(x => x.Password)
            .NotEmpty()
            .MinimumLength(12).WithMessage("Password must be at least 12 characters.")
            .MaximumLength(256)
            .Must(p => p.Any(char.IsUpper) && p.Any(char.IsLower) && p.Any(char.IsDigit))
            .WithMessage("Password must include upper- and lower-case letters and a number.");
    }
}
