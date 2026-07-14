using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WireHQ.Application.Abstractions;
using WireHQ.Application.Abstractions.Persistence;
using WireHQ.Application.Abstractions.Security;
using WireHQ.Application.Common.Email;
using WireHQ.Application.Common.Messaging;
using WireHQ.Domain.Identity;
using WireHQ.Domain.ValueObjects;
using WireHQ.Shared.Results;

namespace WireHQ.Application.Features.Authentication.ForgotPassword;

/// <summary>
/// Starts a password reset. Always succeeds (HTTP 202) regardless of whether the account exists,
/// so it never reveals which emails are registered (anti-enumeration). (docs/06-api-design.md)
/// </summary>
public sealed record ForgotPasswordCommand(string Email, string? TurnstileToken = null) : ICommand, ICaptchaProtected;

public sealed class ForgotPasswordCommandHandler(
    IApplicationDbContext dbContext,
    ITokenService tokenService,
    IDateTimeProvider clock,
    IEmailSender emailSender,
    IClientUrlBuilder urls,
    ILogger<ForgotPasswordCommandHandler> logger)
    : ICommandHandler<ForgotPasswordCommand>
{
    public async Task<Result> Handle(ForgotPasswordCommand command, CancellationToken cancellationToken)
    {
        var emailResult = Email.Create(command.Email);
        if (emailResult.IsFailure)
        {
            return Result.Success(); // never reveal validity
        }

        var email = emailResult.Value;
        var user = await dbContext.Users
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.Email.Value == email.Value && !u.IsDeleted, cancellationToken);

        if (user is not null)
        {
            // Invalidate any outstanding reset tokens, then issue a fresh single-use one.
            var outstanding = await dbContext.PasswordResetTokens
                .Where(t => t.UserId == user.Id && t.UsedAtUtc == null)
                .ToListAsync(cancellationToken);
            foreach (var token in outstanding)
            {
                token.Consume(clock.UtcNow);
            }

            var raw = tokenService.IssueRefreshToken(clock.UtcNow.AddHours(1));
            dbContext.PasswordResetTokens.Add(PasswordResetToken.Issue(user.Id, raw.Hash, raw.ExpiresAtUtc));

            try
            {
                await emailSender.SendAsync(
                    EmailTemplates.PasswordReset(email.Value, urls.ResetPasswordUrl(raw.Value)),
                    cancellationToken);
            }
            catch (Exception ex)
            {
                // A mail failure must not reveal anything to the caller (anti-enumeration) or fail the request.
                logger.LogWarning(ex, "Failed to send password-reset email.");
            }
        }

        return Result.Success();
    }
}
