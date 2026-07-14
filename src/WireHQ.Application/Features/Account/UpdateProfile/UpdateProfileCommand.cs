using FluentValidation;
using Microsoft.EntityFrameworkCore;
using WireHQ.Application.Abstractions;
using WireHQ.Application.Abstractions.Persistence;
using WireHQ.Application.Common.Messaging;
using WireHQ.Domain.Auditing;
using WireHQ.Domain.Identity;
using WireHQ.Shared.Results;

namespace WireHQ.Application.Features.Account.UpdateProfile;

/// <summary>Updates the signed-in user's name + optional profile details (username, job title, phone, etc.).</summary>
public sealed record UpdateProfileCommand(
    string FirstName,
    string LastName,
    string? Username,
    string? JobTitle,
    string? Phone,
    string? Timezone,
    string? Language) : ICommand;

public sealed class UpdateProfileCommandValidator : AbstractValidator<UpdateProfileCommand>
{
    public UpdateProfileCommandValidator()
    {
        RuleFor(x => x.FirstName).NotEmpty().WithMessage("First name is required.").MaximumLength(User.MaxNamePartLength);
        RuleFor(x => x.LastName).NotEmpty().WithMessage("Last name is required.").MaximumLength(User.MaxNamePartLength);
        RuleFor(x => x.Username)
            .MaximumLength(User.MaxUsernameLength)
            .Matches("^[a-zA-Z0-9._-]+$").WithMessage("Username may contain only letters, numbers and . _ -")
            .When(x => !string.IsNullOrWhiteSpace(x.Username));
        RuleFor(x => x.JobTitle).MaximumLength(User.MaxProfileFieldLength);
        RuleFor(x => x.Phone).MaximumLength(User.MaxProfileFieldLength);
        RuleFor(x => x.Timezone).MaximumLength(User.MaxProfileFieldLength);
        RuleFor(x => x.Language).MaximumLength(16);
    }
}

public sealed class UpdateProfileCommandHandler(
    IApplicationDbContext dbContext,
    ICurrentUser currentUser,
    IAuditWriter audit)
    : ICommandHandler<UpdateProfileCommand>
{
    private static readonly Error NotAuthenticated = Error.Unauthorized("auth.unauthenticated", "Authentication is required.");

    // A taken username lands on the `username` field in the UI (RFC 9457 errors map).
    private static readonly Error UsernameTaken =
        new ValidationError(new Dictionary<string, string[]> { ["username"] = ["That username is already taken."] });

    public async Task<Result> Handle(UpdateProfileCommand command, CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId)
        {
            return NotAuthenticated;
        }

        var user = await dbContext.Users
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.Id == userId && !u.IsDeleted, cancellationToken);

        if (user is null)
        {
            return NotAuthenticated;
        }

        var nameResult = user.SetName(command.FirstName, command.LastName);
        if (nameResult.IsFailure)
        {
            return nameResult;
        }

        var username = string.IsNullOrWhiteSpace(command.Username) ? null : command.Username.Trim();
        if (username is not null)
        {
            var taken = await dbContext.Users
                .IgnoreQueryFilters()
                .AnyAsync(u => u.Id != userId && u.Username == username, cancellationToken);
            if (taken)
            {
                return UsernameTaken;
            }
        }

        user.SetProfileDetails(username, command.JobTitle, command.Phone, command.Timezone, command.Language);

        audit.Record("account.profile_updated", AuditOutcome.Success, nameof(User), userId.ToString());
        return Result.Success();
    }
}
