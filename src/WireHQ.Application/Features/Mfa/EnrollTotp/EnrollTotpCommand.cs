using Microsoft.EntityFrameworkCore;
using WireHQ.Application.Abstractions;
using WireHQ.Application.Abstractions.Persistence;
using WireHQ.Application.Abstractions.Security;
using WireHQ.Application.Common.Messaging;
using WireHQ.Domain.Identity;
using WireHQ.Domain.Sessions;
using WireHQ.Shared.Results;

namespace WireHQ.Application.Features.Mfa.EnrollTotp;

/// <summary>Begins TOTP enrolment for the current user: creates a secret + QR, stored unconfirmed.</summary>
public sealed record EnrollTotpCommand : ICommand<EnrollTotpResponse>;

public sealed record EnrollTotpResponse(string Secret, string OtpAuthUri, string QrCodePngBase64);

public sealed class EnrollTotpCommandHandler(
    IApplicationDbContext dbContext,
    ICurrentUser currentUser,
    ITotpService totpService,
    ISecretProtector secretProtector)
    : ICommandHandler<EnrollTotpCommand, EnrollTotpResponse>
{
    public async Task<Result<EnrollTotpResponse>> Handle(EnrollTotpCommand command, CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId)
        {
            return MfaErrors.NotAuthenticated;
        }

        var user = await dbContext.Users
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.Id == userId && !u.IsDeleted, cancellationToken);

        if (user is null)
        {
            return MfaErrors.NotAuthenticated;
        }

        if (user.MfaEnabled)
        {
            return MfaErrors.AlreadyEnabled;
        }

        // Clear any half-finished enrolments so re-starting is clean.
        var pending = await dbContext.MfaCredentials
            .Where(c => c.UserId == userId && c.Type == MfaCredentialType.Totp && !c.IsConfirmed)
            .ToListAsync(cancellationToken);
        dbContext.MfaCredentials.RemoveRange(pending);

        var enrolment = totpService.CreateEnrolment(user.Email.Value);
        var credential = MfaCredential.CreateTotp(userId, secretProtector.Protect(enrolment.Secret), "Authenticator app");
        dbContext.MfaCredentials.Add(credential);

        return new EnrollTotpResponse(enrolment.Secret, enrolment.OtpAuthUri, enrolment.QrCodePngBase64);
    }
}

public static class MfaErrors
{
    public static readonly Error NotAuthenticated = Error.Unauthorized("auth.unauthenticated", "Authentication is required.");
    public static readonly Error AlreadyEnabled = Error.Conflict("mfa.already_enabled", "Multi-factor authentication is already enabled.");
    public static readonly Error NotEnrolling = Error.Conflict("mfa.not_enrolling", "Start MFA enrolment first.");
    public static readonly Error InvalidCode = Error.Validation("mfa.invalid_code", "That code is incorrect or has expired.");
    public static readonly Error NotEnabled = Error.Conflict("mfa.not_enabled", "Multi-factor authentication is not enabled.");
    public static readonly Error InvalidPassword = Error.Unauthorized("auth.invalid_credentials", "Incorrect password.");
}
