using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WireHQ.Application.Abstractions;
using WireHQ.Application.Abstractions.Notifications;
using WireHQ.Application.Abstractions.Persistence;
using WireHQ.Application.Abstractions.Security;
using WireHQ.Application.Common.Messaging;
using WireHQ.Application.Entitlements;
using WireHQ.Application.Features.Notifications;
using WireHQ.Domain.Auditing;
using WireHQ.Domain.Identity;
using WireHQ.Domain.Notifications;
using WireHQ.Domain.ValueObjects;
using WireHQ.Shared.Results;

namespace WireHQ.Application.Features.Account.PhoneVerification;

// Phone-ownership verification for SMS alerts (docs/36-notifications.md §5, SMS Wave 4, Slice B). Two org-scoped,
// authenticated, entitlement-gated (notifications.sms) endpoints:
//   • Start   — issue + send a one-time code through the org's OWN provider, HARDENED: per-user cooldown, per-user /
//               per-org / per-number rate limits, the durable SMS cap (checked+reserved BEFORE the provider call), a
//               single-use ~10-min code stored HASHED only, and a NON-ORACULAR generic outcome for the sensitive paths.
//   • Confirm — constant-time code check with a hard attempt cap; on success records the verified number (unique
//               across users). Every failure collapses to one generic error (no wrong/expired/locked oracle).
// The verification code is separate from consent — proving ownership is NOT opting in (docs/36 §4); the SMS-alerts
// consent toggle is its own audited action.

// --- Start: issue + send a verification code ---

public sealed record StartPhoneVerificationCommand(string PhoneNumber) : ICommand, IRequiresFeature
{
    public string RequiredFeature => PlanFeatures.NotificationsSms;
}

public sealed class StartPhoneVerificationCommandHandler(
    IApplicationDbContext dbContext,
    ICurrentUser currentUser,
    ITenantContext tenant,
    IDateTimeProvider clock,
    ISmsProvider smsProvider,
    ISecretProtector secretProtector,
    SmsNotificationOptions smsOptions,
    ILogger<StartPhoneVerificationCommandHandler> logger)
    : ICommandHandler<StartPhoneVerificationCommand>
{
    // Anti-abuse windows before ANY paid send (docs/36 §5). Durable — derived from phone_verifications timestamps, so
    // they hold across replicas/restarts (the reason the cap itself is durable — docs/36 §7).
    private static readonly TimeSpan ResendCooldown = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan RateWindow = TimeSpan.FromHours(1);
    private const int MaxPerUserPerWindow = 5;
    private const int MaxPerNumberPerWindow = 5;
    private const int MaxPerOrgPerWindow = 100;

    private static readonly Error NotAuthenticated = Error.Unauthorized("auth.unauthenticated", "Authentication is required.");
    private static readonly Error NoOrg = Error.Forbidden("auth.tenant_required", "No active organization.");
    private static readonly Error NotConfigured = Error.Validation("notifications.sms_not_configured", "SMS is not configured for this organization yet.");
    private static readonly Error Cooldown = Error.Validation("account.phone_verification_cooldown", "Please wait a moment before requesting another code.");

    public async Task<Result> Handle(StartPhoneVerificationCommand command, CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId)
        {
            return NotAuthenticated;
        }

        if (tenant.OrganizationId is not { } organizationId)
        {
            return NoOrg;
        }

        // Format validation is a client-input error (not a number oracle), so it may be specific.
        var phoneResult = Domain.ValueObjects.PhoneNumber.Create(command.PhoneNumber);
        if (phoneResult.IsFailure)
        {
            return phoneResult.Error;
        }

        var phone = phoneResult.Value;
        var now = clock.UtcNow;

        // The org must have a working provider before we can send. Not a number oracle, so surface it to the admin.
        var config = await dbContext.NotificationChannelConfigs
            .FirstOrDefaultAsync(c => c.ChannelKind == ChannelKind.Sms, cancellationToken);
        if (config is not { Enabled: true }
            || string.IsNullOrWhiteSpace(config.CredentialCiphertext) || string.IsNullOrWhiteSpace(config.FromValue))
        {
            return NotConfigured;
        }

        // Per-user cooldown — about the ACTOR's own recent request, so a specific "please wait" is safe (no enumeration).
        var lastForUser = await dbContext.PhoneVerifications
            .OrderByDescending(v => v.CreatedAtUtc)
            .FirstOrDefaultAsync(v => v.UserId == userId, cancellationToken);
        if (lastForUser is not null && lastForUser.CreatedAtUtc > now - ResendCooldown)
        {
            return Cooldown;
        }

        // Sensitive gates below are SILENT — they return the SAME generic success as a real send so the endpoint can't
        // be used to enumerate numbers / suppression / capacity (docs/36 §5). The user simply never receives a code.
        var windowStart = now - RateWindow;
        var perUser = await dbContext.PhoneVerifications.CountAsync(v => v.UserId == userId && v.CreatedAtUtc > windowStart, cancellationToken);
        var perNumber = await dbContext.PhoneVerifications.CountAsync(v => v.PhoneE164 == phone.Value && v.CreatedAtUtc > windowStart, cancellationToken);
        var perOrg = await dbContext.PhoneVerifications.CountAsync(v => v.CreatedAtUtc > windowStart, cancellationToken);
        if (perUser >= MaxPerUserPerWindow || perNumber >= MaxPerNumberPerWindow || perOrg >= MaxPerOrgPerWindow)
        {
            logger.LogInformation("Phone verification rate-limited for org {OrganizationId}.", organizationId);
            return Result.Success();
        }

        // A verified number is unique across users (docs/36 §2). If someone else already owns it, stay SILENT — do not
        // reveal that the number is taken. (The number's own owner re-verifying is allowed.)
        var takenByAnother = await dbContext.Users
            .IgnoreQueryFilters()
            .AnyAsync(u => u.Id != userId && !u.IsDeleted && u.PhoneVerified && u.PhoneE164!.Value == phone.Value, cancellationToken);
        if (takenByAnother)
        {
            return Result.Success();
        }

        // Suppression precondition (docs/36 §4.3/§4.4, Slice D): if (org, phone) is actively opted out, stay SILENT and
        // send nothing — the OTP path must respect STOP exactly like the alert path (a suppressed number can't be
        // re-verified into the audience). Same generic success response (non-oracular) as every other skip above.
        if (await SmsSuppression.IsSuppressedAsync(dbContext, organizationId, phone.Value, cancellationToken))
        {
            return Result.Success();
        }

        // Durable SMS cap: check + RESERVE before the provider call (docs/36 §7). Clamped to a finite floor + hard max.
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var usage = await dbContext.NotificationChannelUsage
            .FirstOrDefaultAsync(u => u.ChannelKind == ChannelKind.Sms && u.DayUtc == today, cancellationToken);
        if (usage is null)
        {
            usage = NotificationChannelUsage.Start(organizationId, ChannelKind.Sms, today);
            dbContext.NotificationChannelUsage.Add(usage);
        }

        var cap = NotificationSendPolicy.ResolveSmsDailyCap(smsOptions.DailyCapPerOrg);
        if (usage.HasReached(cap))
        {
            // Cap reached — stay SILENT (don't reveal capacity). The row is not persisted since we don't SaveChanges.
            return Result.Success();
        }

        // Issue the challenge: a 6-digit code, stored ONLY as a salted hash — the domain hashes it with the row id (docs/36 §5).
        var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
        var verification = Domain.Identity.PhoneVerification.Issue(organizationId, userId, phone.Value, code, now);

        // Supersede any still-active challenge for this user (single-use hygiene).
        if (lastForUser is not null && lastForUser.IsActive(now))
        {
            lastForUser.Supersede(now);
        }

        dbContext.PhoneVerifications.Add(verification);
        usage.Increment(); // count the OTP against the day's budget BEFORE the send (docs/36 §5)
        await dbContext.SaveChangesAsync(cancellationToken);

        // DARK-BOUNDARY GATE (docs/36 §2/§10): until the operator enables SMS sending (Slice D flips
        // Notifications:Sms:Enabled), SKIP the provider call entirely. The challenge + cap reservation + rate-limit
        // accounting all stand and the response is the SAME generic success (non-oracular) — but no OTP goes out, so no
        // phone can verify, so ResolveSmsAudienceAsync stays empty and the channel is provably dark in CODE, not by
        // governance alone. The A2P pre-send gate applies here too (docs/36 §4.6): until the operator attests their
        // campaign, no OTP goes out — so a phone can't be verified into the audience before A2P is in place.
        // InboundReady gates the OTP for the same reason it gates alerts, and arguably more sharply: verifying a phone
        // is what PUTS someone into the SMS audience, so allowing it before the STOP webhook is demonstrably working
        // would enrol subscribers we could not then honour a STOP from. Same non-oracular behaviour — the challenge,
        // cooldown and cap reservation all still stand, only the provider call is skipped.
        if (smsOptions.Enabled && config.A2pAttested && config.InboundReady)
        {
            // Send through the org's OWN provider. The provider collapses all transport/provider errors to a fixed
            // generic string, so nothing (number/credential/code) can leak (docs/36 §5). A send failure is swallowed —
            // the row + reservation stand; the user retries after the cooldown. The response is generic either way.
            try
            {
                var credential = secretProtector.Unprotect(config.CredentialCiphertext);
                var body = SmsNotificationDisclosure.RenderVerificationBody(code);
                await smsProvider.SendAsync(new SmsSendRequest(phone.Value, config.FromValue, body, credential), cancellationToken);
            }
            // A provider timeout throws TaskCanceledException — an OperationCanceledException — with our own token
            // uncancelled, so without the second clause it escapes to a 500. That matters more here than it looks:
            // every other outcome on this path (rate-limited, number already verified, STOP-suppressed, cap reached)
            // deliberately returns the SAME generic success, and a 500 can only be produced by a request that actually
            // reached the send — telling a caller the number is unowned, unsuppressed and under cap. The very oracle
            // this handler is built to deny.
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Phone verification send failed for org {OrganizationId}.", organizationId);
            }
        }

        return Result.Success();
    }
}

// --- Confirm: check the code, record the verified number ---

public sealed record ConfirmPhoneVerificationCommand(string Code) : ICommand, IRequiresFeature
{
    public string RequiredFeature => PlanFeatures.NotificationsSms;
}

public sealed class ConfirmPhoneVerificationCommandHandler(
    IApplicationDbContext dbContext,
    ICurrentUser currentUser,
    ITenantContext tenant,
    IDateTimeProvider clock,
    IAuditWriter audit)
    : ICommandHandler<ConfirmPhoneVerificationCommand>
{
    private static readonly Error NotAuthenticated = Error.Unauthorized("auth.unauthenticated", "Authentication is required.");
    private static readonly Error NoOrg = Error.Forbidden("auth.tenant_required", "No active organization.");

    public async Task<Result> Handle(ConfirmPhoneVerificationCommand command, CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId)
        {
            return NotAuthenticated;
        }

        if (tenant.OrganizationId is not { } organizationId)
        {
            return NoOrg;
        }

        var now = clock.UtcNow;

        // The most recent challenge for this (org, user). All failure paths below return the SAME generic error.
        var verification = await dbContext.PhoneVerifications
            .OrderByDescending(v => v.CreatedAtUtc)
            .FirstOrDefaultAsync(v => v.UserId == userId, cancellationToken);
        if (verification is null)
        {
            return PhoneVerificationErrors.Failed;
        }

        var presentedHash = Domain.Identity.PhoneVerification.HashCode(verification.Id, (command.Code ?? string.Empty).Trim());
        var outcome = verification.Confirm(presentedHash, now);
        if (outcome != PhoneVerificationOutcome.Verified)
        {
            // Persist the attempt increment / burn, but reveal nothing (docs/36 §5).
            await dbContext.SaveChangesAsync(cancellationToken);
            return PhoneVerificationErrors.Failed;
        }

        var phoneResult = Domain.ValueObjects.PhoneNumber.Create(verification.PhoneE164);
        if (phoneResult.IsFailure)
        {
            return PhoneVerificationErrors.Failed;
        }

        // Enforce cross-user uniqueness at the moment of verifying (docs/36 §2). A race is still backstopped by the
        // filtered unique index; a conflict here returns the generic error (no "already registered" oracle).
        var takenByAnother = await dbContext.Users
            .IgnoreQueryFilters()
            .AnyAsync(u => u.Id != userId && !u.IsDeleted && u.PhoneVerified && u.PhoneE164!.Value == verification.PhoneE164, cancellationToken);
        if (takenByAnother)
        {
            return PhoneVerificationErrors.Failed;
        }

        var user = await dbContext.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user is null)
        {
            return NotAuthenticated;
        }

        user.SetVerifiedPhone(phoneResult.Value, now);

        // Ownership proof — audited (masked number, un-routable notifications.* action). NOT a consent event.
        audit.Record("notifications.phone_verified", AuditOutcome.Success, nameof(User), userId.ToString(),
            new { Phone = MaskPhone(verification.PhoneE164) });

        try
        {
            // Save explicitly so the filtered-unique-index race (two users verifying the same number concurrently)
            // surfaces HERE and collapses to the generic error — a 400, never a 500, and non-oracular (docs/36 §2/§5).
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            return PhoneVerificationErrors.Failed;
        }

        return Result.Success();
    }

    internal static string MaskPhone(string e164)
    {
        if (string.IsNullOrEmpty(e164) || e164.Length <= 4)
        {
            return "•••";
        }

        return new string('•', e164.Length - 4) + e164[^4..];
    }
}

// --- Read: the current user's phone / SMS-provider status (never a code, never a credential) ---

public sealed record PhoneVerificationStatusResponse(
    bool PhoneVerified, string? PhoneMasked, bool ProviderConfigured, string DisclosureText, string DisclosureVersion);

public sealed record GetPhoneVerificationStatusQuery : IQuery<PhoneVerificationStatusResponse>;

public sealed class GetPhoneVerificationStatusQueryHandler(IApplicationDbContext dbContext, ICurrentUser currentUser)
    : IQueryHandler<GetPhoneVerificationStatusQuery, PhoneVerificationStatusResponse>
{
    private static readonly Error NotAuthenticated = Error.Unauthorized("auth.unauthenticated", "Authentication is required.");

    public async Task<Result<PhoneVerificationStatusResponse>> Handle(GetPhoneVerificationStatusQuery query, CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId)
        {
            return NotAuthenticated;
        }

        var user = await dbContext.Users.AsNoTracking()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.Id == userId && !u.IsDeleted, cancellationToken);

        var providerConfigured = await dbContext.NotificationChannelConfigs.AsNoTracking()
            .AnyAsync(c => c.ChannelKind == ChannelKind.Sms && c.Enabled && c.CredentialCiphertext != null && c.FromValue != null, cancellationToken);

        var verified = user?.PhoneVerified == true && user.PhoneE164 != null;
        return new PhoneVerificationStatusResponse(
            verified,
            verified ? ConfirmPhoneVerificationCommandHandler.MaskPhone(user!.PhoneE164!.Value) : null,
            providerConfigured,
            SmsNotificationDisclosure.DisclosureText,
            SmsNotificationDisclosure.DisclosureVersion);
    }
}
