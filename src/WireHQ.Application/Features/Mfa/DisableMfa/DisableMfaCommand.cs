using Microsoft.EntityFrameworkCore;
using WireHQ.Application.Abstractions;
using WireHQ.Application.Abstractions.Persistence;
using WireHQ.Application.Abstractions.Security;
using WireHQ.Application.Common.Messaging;
using WireHQ.Application.Features.Mfa.EnrollTotp;
using WireHQ.Domain.Auditing;
using WireHQ.Shared.Results;

namespace WireHQ.Application.Features.Mfa.DisableMfa;

/// <summary>Disables MFA after re-authenticating with the account password, and purges all factors/codes.</summary>
public sealed record DisableMfaCommand(string Password) : ICommand;

public sealed class DisableMfaCommandHandler(
    IApplicationDbContext dbContext,
    ICurrentUser currentUser,
    IPasswordHasher passwordHasher,
    IAuditWriter audit)
    : ICommandHandler<DisableMfaCommand>
{
    public async Task<Result> Handle(DisableMfaCommand command, CancellationToken cancellationToken)
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

        if (!user.MfaEnabled)
        {
            return MfaErrors.NotEnabled;
        }

        if (passwordHasher.Verify(command.Password, user.PasswordHash) == PasswordVerificationResult.Failed)
        {
            return MfaErrors.InvalidPassword;
        }

        user.DisableMfa();

        var credentials = await dbContext.MfaCredentials.Where(c => c.UserId == userId).ToListAsync(cancellationToken);
        dbContext.MfaCredentials.RemoveRange(credentials);

        var recoveryCodes = await dbContext.RecoveryCodes.Where(c => c.UserId == userId).ToListAsync(cancellationToken);
        dbContext.RecoveryCodes.RemoveRange(recoveryCodes);

        audit.Record("mfa.disabled", AuditOutcome.Success, nameof(Domain.Identity.User), userId.ToString());
        return Result.Success();
    }
}
