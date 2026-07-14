using FluentValidation;
using Microsoft.EntityFrameworkCore;
using WireHQ.Application.Abstractions;
using WireHQ.Application.Abstractions.Persistence;
using WireHQ.Application.Abstractions.Security;
using WireHQ.Application.Common.Messaging;
using WireHQ.Application.Features.Mfa.EnrollTotp;
using WireHQ.Domain.Auditing;
using WireHQ.Domain.Sessions;
using WireHQ.Shared.Results;

namespace WireHQ.Application.Features.Mfa.ConfirmTotp;

/// <summary>Confirms TOTP enrolment with the first code, enables MFA, and returns one-time recovery codes.</summary>
public sealed record ConfirmTotpCommand(string Code) : ICommand<ConfirmTotpResponse>;

public sealed record ConfirmTotpResponse(IReadOnlyList<string> RecoveryCodes);

public sealed class ConfirmTotpCommandValidator : AbstractValidator<ConfirmTotpCommand>
{
    public ConfirmTotpCommandValidator() => RuleFor(x => x.Code).NotEmpty().Length(6);
}

public sealed class ConfirmTotpCommandHandler(
    IApplicationDbContext dbContext,
    ICurrentUser currentUser,
    ITotpService totpService,
    ISecretProtector secretProtector,
    IRecoveryCodeService recoveryCodeService,
    IDateTimeProvider clock,
    IAuditWriter audit)
    : ICommandHandler<ConfirmTotpCommand, ConfirmTotpResponse>
{
    public async Task<Result<ConfirmTotpResponse>> Handle(ConfirmTotpCommand command, CancellationToken cancellationToken)
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

        var credential = await dbContext.MfaCredentials
            .Where(c => c.UserId == userId && c.Type == MfaCredentialType.Totp && !c.IsConfirmed)
            .OrderByDescending(c => c.CreatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

        if (credential is null)
        {
            return MfaErrors.NotEnrolling;
        }

        var secret = secretProtector.Unprotect(credential.Secret);
        if (!totpService.VerifyCode(secret, command.Code))
        {
            return MfaErrors.InvalidCode;
        }

        credential.Confirm(clock.UtcNow);
        user.EnableMfa();

        // Replace any prior recovery codes with a fresh set, returned once.
        var oldCodes = await dbContext.RecoveryCodes.Where(c => c.UserId == userId).ToListAsync(cancellationToken);
        dbContext.RecoveryCodes.RemoveRange(oldCodes);

        var generated = recoveryCodeService.Generate();
        foreach (var pair in generated)
        {
            dbContext.RecoveryCodes.Add(RecoveryCode.Create(userId, pair.CodeHash));
        }

        audit.Record("mfa.enabled", AuditOutcome.Success, nameof(Domain.Identity.User), userId.ToString());

        return new ConfirmTotpResponse(generated.Select(g => g.Code).ToArray());
    }
}
