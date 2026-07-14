using FluentValidation;
using Microsoft.EntityFrameworkCore;
using WireHQ.Application.Abstractions;
using WireHQ.Application.Abstractions.Persistence;
using WireHQ.Application.Abstractions.Security;
using WireHQ.Application.Common.Messaging;
using WireHQ.Domain.Auditing;
using WireHQ.Shared.Results;

namespace WireHQ.Application.Features.Account.ChangePassword;

/// <summary>Changes the signed-in user's password after re-entering the current one; revokes other sessions.</summary>
public sealed record ChangePasswordCommand(string CurrentPassword, string NewPassword) : ICommand;

public sealed class ChangePasswordCommandValidator : AbstractValidator<ChangePasswordCommand>
{
    public ChangePasswordCommandValidator()
    {
        RuleFor(x => x.CurrentPassword).NotEmpty();
        RuleFor(x => x.NewPassword)
            .NotEmpty()
            .MinimumLength(12).WithMessage("Password must be at least 12 characters.")
            .Must(p => p.Any(char.IsUpper) && p.Any(char.IsLower) && p.Any(char.IsDigit))
            .WithMessage("Password must include upper- and lower-case letters and a number.");
    }
}

public sealed class ChangePasswordCommandHandler(
    IApplicationDbContext dbContext,
    ICurrentUser currentUser,
    IPasswordHasher passwordHasher,
    IDateTimeProvider clock,
    IAuditWriter audit)
    : ICommandHandler<ChangePasswordCommand>
{
    private static readonly Error NotAuthenticated = Error.Unauthorized("auth.unauthenticated", "Authentication is required.");
    private static readonly Error InvalidCurrent = Error.Unauthorized("auth.invalid_credentials", "Your current password is incorrect.");

    public async Task<Result> Handle(ChangePasswordCommand command, CancellationToken cancellationToken)
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

        if (passwordHasher.Verify(command.CurrentPassword, user.PasswordHash) == PasswordVerificationResult.Failed)
        {
            return InvalidCurrent;
        }

        user.ChangePassword(passwordHasher.Hash(command.NewPassword));

        // Revoke every other session/refresh token; the current session stays signed in.
        var currentSessionId = currentUser.SessionId;
        var otherSessions = await dbContext.UserSessions
            .IgnoreQueryFilters()
            .Where(s => s.UserId == userId && s.RevokedAtUtc == null && s.Id != currentSessionId)
            .ToListAsync(cancellationToken);
        foreach (var session in otherSessions)
        {
            session.Revoke(clock.UtcNow);
        }

        var otherTokens = await dbContext.RefreshTokens
            .IgnoreQueryFilters()
            .Where(t => t.UserId == userId && t.RevokedAtUtc == null && t.SessionId != currentSessionId)
            .ToListAsync(cancellationToken);
        foreach (var token in otherTokens)
        {
            token.Revoke(clock.UtcNow);
        }

        audit.Record("account.password_changed", AuditOutcome.Success, nameof(Domain.Identity.User), userId.ToString());
        return Result.Success();
    }
}
