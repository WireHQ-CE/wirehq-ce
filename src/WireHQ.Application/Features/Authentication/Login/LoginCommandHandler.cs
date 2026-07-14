using Microsoft.EntityFrameworkCore;
using WireHQ.Application.Abstractions;
using WireHQ.Application.Abstractions.Authentication;
using WireHQ.Application.Abstractions.Persistence;
using WireHQ.Application.Abstractions.Security;
using WireHQ.Application.Common.Messaging;
using WireHQ.Domain.Auditing;
using WireHQ.Domain.Identity;
using WireHQ.Domain.Memberships;
using WireHQ.Shared.Results;

namespace WireHQ.Application.Features.Authentication.Login;

public sealed class LoginCommandHandler(
    IApplicationDbContext dbContext,
    IPasswordHasher passwordHasher,
    AuthSessionService sessions,
    IDateTimeProvider clock,
    IRequestContext request,
    IAuditWriter audit,
    IEnumerable<IExternalPasswordAuthenticator> externalAuthenticators)
    : ICommandHandler<LoginCommand, LoginResponse>
{
    // A throwaway, valid-format hash to verify against when the account doesn't exist, so the response
    // timing doesn't reveal whether an email is registered (anti-enumeration). Matches the current
    // PasswordHasher algorithm (Argon2id, m=19456 KiB / t=2 / p=1) so a missing account costs the same
    // memory-hard work as a real one. Format: ARGON2ID$memoryKib$passes$parallelism$salt$hash. (docs/04-security.md)
    private const string DummyHash =
        "ARGON2ID$19456$2$1$AAAAAAAAAAAAAAAAAAAAAA==$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";

    public async Task<Result<LoginResponse>> Handle(LoginCommand command, CancellationToken cancellationToken)
    {
        var user = await dbContext.Users
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.Email.Value == command.Email.Trim().ToLower() && !u.IsDeleted, cancellationToken);

        if (user is null)
        {
            passwordHasher.Verify(command.Password, DummyHash); // equalize timing
            return UserErrors.InvalidCredentials;
        }

        if (user.IsLockedOut(clock.UtcNow))
        {
            audit.Record("auth.login", AuditOutcome.Failure, nameof(User), user.Id.ToString(), new { reason = "locked" });
            await dbContext.SaveChangesAsync(cancellationToken);
            return UserErrors.AccountLocked;
        }

        // An external credential authority (a synced LDAP/AD directory — docs/24-ldap-authentication.md) may
        // govern this user's password. Consult each; the first that claims the user is authoritative — there is no
        // local-password fallback (A-2). The enumerable is empty in the Community Edition, so login is unchanged.
        foreach (var authenticator in externalAuthenticators)
        {
            var external = await authenticator.AuthenticateAsync(user, command.Password, cancellationToken);
            if (external.Status == ExternalPasswordStatus.NotApplicable)
            {
                continue;
            }

            if (external.Status == ExternalPasswordStatus.Failed)
            {
                user.RegisterFailedSignIn(clock.UtcNow);
                audit.Record("auth.login", AuditOutcome.Failure, nameof(User), user.Id.ToString(), new { source = "directory" });
                await dbContext.SaveChangesAsync(cancellationToken);
                return UserErrors.InvalidCredentials;
            }

            // Succeeded: the directory verified the password. An LDAP bind is a single factor, so WireHQ's own MFA
            // gate still applies (A-5) — mint on the directory-linked membership via the federated session path.
            if (user.Status == UserStatus.Suspended)
            {
                return UserErrors.AccountLocked;
            }

            var linkedMembership = await dbContext.Memberships
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(
                    m => m.Id == external.MembershipId && m.Status == MembershipStatus.Active && !m.IsDeleted, cancellationToken);
            if (linkedMembership is null)
            {
                return UserErrors.InvalidCredentials; // the linked membership is gone — deny rather than fall back
            }

            user.RegisterSuccessfulSignIn(clock.UtcNow);
            var federated = await sessions.StartFederatedSessionAsync(
                user, linkedMembership, mfaAlreadySatisfied: false, request.IpAddress, request.UserAgent, cancellationToken);

            audit.Record("auth.login", AuditOutcome.Success, nameof(User), user.Id.ToString(),
                new { source = "directory", mfaRequired = federated.MfaRequired });

            return new LoginResponse(
                federated.AccessToken.Value,
                federated.AccessToken.ExpiresInSeconds,
                federated.RefreshToken,
                federated.MfaRequired);
        }

        var verification = passwordHasher.Verify(command.Password, user.PasswordHash);
        if (verification == PasswordVerificationResult.Failed)
        {
            user.RegisterFailedSignIn(clock.UtcNow);
            audit.Record("auth.login", AuditOutcome.Failure, nameof(User), user.Id.ToString());
            // Persist the failed-attempt/lockout state + audit explicitly: a failed login returns
            // a failure Result, so the UnitOfWork behavior would not otherwise save them.
            await dbContext.SaveChangesAsync(cancellationToken);
            return UserErrors.InvalidCredentials;
        }

        if (user.Status == UserStatus.Suspended)
        {
            return UserErrors.AccountLocked;
        }

        // Transparently upgrade the stored hash if its parameters are now outdated.
        if (verification == PasswordVerificationResult.SuccessRehashNeeded)
        {
            user.ChangePassword(passwordHasher.Hash(command.Password));
        }

        user.RegisterSuccessfulSignIn(clock.UtcNow);

        var issued = await sessions.StartSessionAsync(user, request.IpAddress, request.UserAgent, cancellationToken);

        audit.Record(
            "auth.login",
            AuditOutcome.Success,
            nameof(User),
            user.Id.ToString(),
            new { mfaRequired = issued.MfaRequired });

        return new LoginResponse(
            issued.AccessToken.Value,
            issued.AccessToken.ExpiresInSeconds,
            issued.RefreshToken,
            issued.MfaRequired);
    }
}
