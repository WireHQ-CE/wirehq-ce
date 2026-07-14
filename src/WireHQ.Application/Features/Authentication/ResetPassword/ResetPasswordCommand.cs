using FluentValidation;
using Microsoft.EntityFrameworkCore;
using WireHQ.Application.Abstractions;
using WireHQ.Application.Abstractions.Persistence;
using WireHQ.Application.Abstractions.Security;
using WireHQ.Application.Common.Messaging;
using WireHQ.Domain.Auditing;
using WireHQ.Shared.Results;

namespace WireHQ.Application.Features.Authentication.ResetPassword;

/// <summary>Completes a password reset with a valid token; rotates credentials and logs out all sessions.</summary>
public sealed record ResetPasswordCommand(string Token, string NewPassword, string? TurnstileToken = null)
    : ICommand, ICaptchaProtected;

public sealed class ResetPasswordCommandValidator : AbstractValidator<ResetPasswordCommand>
{
    public ResetPasswordCommandValidator()
    {
        RuleFor(x => x.Token).NotEmpty();
        RuleFor(x => x.NewPassword)
            .NotEmpty()
            .MinimumLength(12).WithMessage("Password must be at least 12 characters.")
            .Must(p => p.Any(char.IsUpper) && p.Any(char.IsLower) && p.Any(char.IsDigit))
            .WithMessage("Password must include upper- and lower-case letters and a number.");
    }
}

public sealed class ResetPasswordCommandHandler(
    IApplicationDbContext dbContext,
    ITokenService tokenService,
    IPasswordHasher passwordHasher,
    IDateTimeProvider clock,
    IAuditWriter audit)
    : ICommandHandler<ResetPasswordCommand>
{
    private static readonly Error InvalidToken =
        Error.Validation("auth.invalid_reset_token", "This reset link is invalid or has expired. Request a new one.");

    public async Task<Result> Handle(ResetPasswordCommand command, CancellationToken cancellationToken)
    {
        var hash = tokenService.HashRefreshToken(command.Token);

        var resetToken = await dbContext.PasswordResetTokens
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.TokenHash == hash, cancellationToken);

        if (resetToken is null || !resetToken.IsActive(clock.UtcNow))
        {
            return InvalidToken;
        }

        var user = await dbContext.Users
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.Id == resetToken.UserId && !u.IsDeleted, cancellationToken);

        if (user is null)
        {
            return InvalidToken;
        }

        user.ChangePassword(passwordHasher.Hash(command.NewPassword));
        // Setting a password via an emailed link proves email control — so an invited (placeholder) user
        // who accepts via this flow is also verified + activated. A no-op for already-verified users.
        user.VerifyEmail();
        resetToken.Consume(clock.UtcNow);

        // A password reset logs the user out everywhere.
        await RevokeAllSessionsAsync(user.Id, cancellationToken);

        audit.Record("auth.password_reset", AuditOutcome.Success, nameof(Domain.Identity.User), user.Id.ToString());
        return Result.Success();
    }

    private async Task RevokeAllSessionsAsync(Guid userId, CancellationToken cancellationToken)
    {
        var sessions = await dbContext.UserSessions
            .IgnoreQueryFilters()
            .Where(s => s.UserId == userId && s.RevokedAtUtc == null)
            .ToListAsync(cancellationToken);
        foreach (var session in sessions)
        {
            session.Revoke(clock.UtcNow);
        }

        var tokens = await dbContext.RefreshTokens
            .IgnoreQueryFilters()
            .Where(t => t.UserId == userId && t.RevokedAtUtc == null)
            .ToListAsync(cancellationToken);
        foreach (var token in tokens)
        {
            token.Revoke(clock.UtcNow);
        }
    }
}
