using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WireHQ.Application.Abstractions;
using WireHQ.Application.Abstractions.Persistence;
using WireHQ.Application.Abstractions.Security;
using WireHQ.Application.Common.Email;
using WireHQ.Application.Common.Messaging;
using WireHQ.Domain.Auditing;
using WireHQ.Domain.Identity;
using WireHQ.Shared.Results;

namespace WireHQ.Application.Features.Authentication.VerifyEmail;

/// <summary>Confirms a registration via the emailed link. Anonymous + token-gated (no captcha needed).</summary>
public sealed record VerifyEmailCommand(string Token) : ICommand;

public sealed class VerifyEmailCommandHandler(
    IApplicationDbContext dbContext,
    ITokenService tokenService,
    IDateTimeProvider clock,
    IAuditWriter audit)
    : ICommandHandler<VerifyEmailCommand>
{
    private static readonly Error InvalidToken =
        Error.Validation("auth.invalid_verification_token", "This verification link is invalid or has expired.");

    public async Task<Result> Handle(VerifyEmailCommand command, CancellationToken cancellationToken)
    {
        var hash = tokenService.HashRefreshToken(command.Token);

        var token = await dbContext.EmailVerificationTokens
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.TokenHash == hash, cancellationToken);

        if (token is null || !token.IsActive(clock.UtcNow))
        {
            return InvalidToken;
        }

        var user = await dbContext.Users
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.Id == token.UserId && !u.IsDeleted, cancellationToken);

        if (user is null)
        {
            return InvalidToken;
        }

        user.VerifyEmail();
        token.Consume(clock.UtcNow);

        audit.Record("auth.email_verified", AuditOutcome.Success, nameof(User), user.Id.ToString());
        return Result.Success();
    }
}

/// <summary>Re-sends the verification email for the signed-in user (if not already verified).</summary>
public sealed record ResendVerificationCommand : ICommand;

public sealed class ResendVerificationCommandHandler(
    IApplicationDbContext dbContext,
    ICurrentUser currentUser,
    ITokenService tokenService,
    IDateTimeProvider clock,
    IEmailSender emailSender,
    IClientUrlBuilder urls,
    ILogger<ResendVerificationCommandHandler> logger)
    : ICommandHandler<ResendVerificationCommand>
{
    public async Task<Result> Handle(ResendVerificationCommand command, CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId)
        {
            return Error.Unauthorized("auth.unauthenticated", "Authentication is required.");
        }

        var user = await dbContext.Users
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.Id == userId && !u.IsDeleted, cancellationToken);

        if (user is null)
        {
            return Error.Unauthorized("auth.unauthenticated", "Authentication is required.");
        }

        if (user.EmailVerified)
        {
            return Result.Success(); // already verified — nothing to do
        }

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
            logger.LogWarning(ex, "Failed to resend verification email.");
        }

        return Result.Success();
    }
}
