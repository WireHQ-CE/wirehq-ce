using FluentValidation;
using Microsoft.EntityFrameworkCore;
using WireHQ.Application.Abstractions;
using WireHQ.Application.Abstractions.Persistence;
using WireHQ.Application.Abstractions.Security;
using WireHQ.Application.Common.Messaging;
using WireHQ.Application.Features.Authentication;
using WireHQ.Domain.Auditing;
using WireHQ.Domain.Sessions;
using WireHQ.Shared.Results;

namespace WireHQ.Application.Features.Authentication.VerifyMfa;

/// <summary>
/// Completes an MFA-pending login: verifies a TOTP code or a single-use recovery code against the
/// current session, marks the session MFA-satisfied, and re-issues a full (permissioned) access token.
/// </summary>
public sealed record VerifyMfaCommand(string Code) : ICommand<VerifyMfaResponse>, ITenantUnscopedRequest;

public sealed record VerifyMfaResponse(string AccessToken, int ExpiresIn);

public sealed class VerifyMfaCommandValidator : AbstractValidator<VerifyMfaCommand>
{
    public VerifyMfaCommandValidator() => RuleFor(x => x.Code).NotEmpty();
}

public sealed class VerifyMfaCommandHandler(
    IApplicationDbContext dbContext,
    ICurrentUser currentUser,
    AuthSessionService sessions,
    ITotpService totpService,
    ISecretProtector secretProtector,
    IRecoveryCodeService recoveryCodeService,
    IDateTimeProvider clock,
    IAuditWriter audit)
    : ICommandHandler<VerifyMfaCommand, VerifyMfaResponse>
{
    private static readonly Error InvalidCode = Error.Unauthorized("auth.invalid_mfa_code", "That code is incorrect or has expired.");
    private static readonly Error NotAuthenticated = Error.Unauthorized("auth.unauthenticated", "Authentication is required.");

    public async Task<Result<VerifyMfaResponse>> Handle(VerifyMfaCommand command, CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId || currentUser.SessionId is not { } sessionId)
        {
            return NotAuthenticated;
        }

        var user = await dbContext.Users
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.Id == userId && !u.IsDeleted, cancellationToken);

        var session = await dbContext.UserSessions
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.Id == sessionId && s.RevokedAtUtc == null, cancellationToken);

        if (user is null || session is null)
        {
            return NotAuthenticated;
        }

        var verified = await VerifyAsync(userId, command.Code.Trim(), cancellationToken);
        if (!verified)
        {
            audit.Record("auth.mfa_verify", AuditOutcome.Failure, nameof(Domain.Identity.User), userId.ToString());
            return InvalidCode;
        }

        session.MarkMfaSatisfied();

        var membership = await sessions.GetDefaultMembershipAsync(userId, cancellationToken);
        var access = await sessions.IssueAccessAsync(user, sessionId, membership, mfaSatisfied: true, cancellationToken);

        audit.Record("auth.mfa_verify", AuditOutcome.Success, nameof(Domain.Identity.User), userId.ToString());

        return new VerifyMfaResponse(access.Value, access.ExpiresInSeconds);
    }

    private async Task<bool> VerifyAsync(Guid userId, string code, CancellationToken cancellationToken)
    {
        var totp = await dbContext.MfaCredentials
            .Where(c => c.UserId == userId && c.Type == MfaCredentialType.Totp && c.IsConfirmed)
            .FirstOrDefaultAsync(cancellationToken);

        if (totp is not null && totpService.VerifyCode(secretProtector.Unprotect(totp.Secret), code))
        {
            totp.MarkUsed(clock.UtcNow);
            return true;
        }

        // Fall back to single-use recovery codes.
        var recoveryCodes = await dbContext.RecoveryCodes
            .Where(c => c.UserId == userId && c.UsedAtUtc == null)
            .ToListAsync(cancellationToken);

        var match = recoveryCodes.FirstOrDefault(c => recoveryCodeService.Verify(code, c.CodeHash));
        if (match is null)
        {
            return false;
        }

        match.Consume(clock.UtcNow);
        return true;
    }
}
