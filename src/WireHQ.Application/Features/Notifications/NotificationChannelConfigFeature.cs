using FluentValidation;
using Microsoft.EntityFrameworkCore;
using WireHQ.Application.Abstractions;
using WireHQ.Application.Abstractions.Persistence;
using WireHQ.Application.Abstractions.Security;
using WireHQ.Application.Authorization;
using WireHQ.Application.Common.Messaging;
using WireHQ.Application.Entitlements;
using WireHQ.Domain.Auditing;
using WireHQ.Domain.Notifications;
using WireHQ.Shared.Results;

namespace WireHQ.Application.Features.Notifications;

// Per-org channel configuration for the Chat Alerts channel (docs/35-notifications.md §4.2/§4.3, Wave 2). Setting a
// destination requires notifications.manage AND the notifications.chat entitlement (so only an entitled org can
// configure the channel). The destination URL is a bearer secret (anyone with a Slack/Teams incoming-webhook URL can
// post), so it is stored but NEVER returned by a query — the read surface reports provider + configured/enabled only.

// --- Set (upsert) the Chat destination ---

public sealed record SetChatDestinationCommand(NotificationProviderKind Provider, string DestinationUrl)
    : ICommand, IAuthorizedRequest, IRequiresFeature
{
    public IReadOnlyCollection<string> RequiredPermissions => [Permissions.Notifications.Manage];

    public string RequiredFeature => PlanFeatures.NotificationsChat;
}

public sealed class SetChatDestinationCommandValidator : AbstractValidator<SetChatDestinationCommand>
{
    public SetChatDestinationCommandValidator()
    {
        RuleFor(x => x.DestinationUrl).NotEmpty().MaximumLength(NotificationChannelConfig.MaxUrlLength);
    }
}

public sealed class SetChatDestinationCommandHandler(
    IApplicationDbContext dbContext, ITenantContext tenant, ISecretProtector secretProtector, IAuditWriter audit)
    : ICommandHandler<SetChatDestinationCommand>
{
    public async Task<Result> Handle(SetChatDestinationCommand command, CancellationToken cancellationToken)
    {
        if (tenant.OrganizationId is not { } organizationId)
        {
            return Error.Forbidden("auth.tenant_required", "No active organization.");
        }

        if (command.Provider is not (NotificationProviderKind.Slack or NotificationProviderKind.Teams))
        {
            return Error.Validation("notification.invalid_provider", "Choose Slack or Microsoft Teams.");
        }

        var url = command.DestinationUrl.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            return Error.Validation("notification.invalid_destination", "Enter a valid https incoming-webhook URL.");
        }

        var config = await dbContext.NotificationChannelConfigs
            .FirstOrDefaultAsync(c => c.ChannelKind == ChannelKind.Chat, cancellationToken);
        if (config is null)
        {
            config = NotificationChannelConfig.Create(organizationId, ChannelKind.Chat);
            dbContext.NotificationChannelConfigs.Add(config);
        }

        // Encrypt the webhook URL at rest — it's a bearer secret, like the SMS credential (docs/35 §4.2; review B-fix).
        config.SetChatDestination(command.Provider, secretProtector.Protect(url), fromValue: null);
        config.Enable();

        audit.Record("notifications.chat_configured", AuditOutcome.Success, nameof(NotificationChannelConfig), config.Id.ToString(),
            new { Provider = command.Provider.ToString() });

        return Result.Success();
    }
}

// --- Set (upsert) the SMS provider (docs/36 §5/§8, Wave 4) ---

// Store the operator's OWN provider credential (encrypted) + sender number. Gated on notifications.manage AND the
// notifications.sms entitlement. The credential is WRITE-ONLY: it is ISecretProtector-encrypted at rest and NEVER
// returned by any query (docs/36 §5) — the read surface reports only "configured" + a masked From. NEVER IPlatformRequest
// (the CE has no platform tier — docs/36 §9). The AccountSid+AuthToken are joined as "AccountSid:AuthToken" (also
// Twilio's Basic-auth userinfo — TwilioSmsProvider splits on the first colon).
public sealed record SetSmsProviderCommand(NotificationProviderKind Provider, string AccountSid, string AuthToken, string FromValue)
    : ICommand, IAuthorizedRequest, IRequiresFeature
{
    public IReadOnlyCollection<string> RequiredPermissions => [Permissions.Notifications.Manage];

    public string RequiredFeature => PlanFeatures.NotificationsSms;
}

public sealed class SetSmsProviderCommandValidator : AbstractValidator<SetSmsProviderCommand>
{
    public SetSmsProviderCommandValidator()
    {
        RuleFor(x => x.AccountSid).NotEmpty().MaximumLength(256);
        RuleFor(x => x.AuthToken).NotEmpty().MaximumLength(512);
        RuleFor(x => x.FromValue).NotEmpty().MaximumLength(NotificationChannelConfig.MaxFromLength);
    }
}

public sealed class SetSmsProviderCommandHandler(
    IApplicationDbContext dbContext, ITenantContext tenant, ISecretProtector secretProtector, IAuditWriter audit)
    : ICommandHandler<SetSmsProviderCommand>
{
    public async Task<Result> Handle(SetSmsProviderCommand command, CancellationToken cancellationToken)
    {
        if (tenant.OrganizationId is not { } organizationId)
        {
            return Error.Forbidden("auth.tenant_required", "No active organization.");
        }

        // Twilio-first (docs/36 §2); Vonage is an additive drop-in in Slice E behind the same seam.
        if (command.Provider != NotificationProviderKind.Twilio)
        {
            return Error.Validation("notification.invalid_provider", "Choose a supported SMS provider.");
        }

        var accountSid = command.AccountSid.Trim();
        var authToken = command.AuthToken.Trim();
        var from = command.FromValue.Trim();
        if (accountSid.Contains(':', StringComparison.Ordinal))
        {
            // The stored credential is "AccountSid:AuthToken", split on the FIRST colon — a colon in the SID would corrupt it.
            return Error.Validation("notification.invalid_credential", "The account identifier must not contain a colon.");
        }

        var config = await dbContext.NotificationChannelConfigs
            .FirstOrDefaultAsync(c => c.ChannelKind == ChannelKind.Sms, cancellationToken);
        if (config is null)
        {
            config = NotificationChannelConfig.Create(organizationId, ChannelKind.Sms);
            dbContext.NotificationChannelConfigs.Add(config);
        }

        // Encrypt at rest; the plaintext credential never leaves this handler (docs/36 §5).
        config.SetSmsProvider(command.Provider, secretProtector.Protect($"{accountSid}:{authToken}"), from);
        config.Enable();

        audit.Record("notifications.sms_configured", AuditOutcome.Success, nameof(NotificationChannelConfig), config.Id.ToString(),
            new { Provider = command.Provider.ToString(), From = SmsProviderStatus.MaskFrom(from) });

        return Result.Success();
    }
}

// --- A2P/10DLC attestation (docs/36 §4.6, Slice D) — the operator's trust statement that unlocks SMS sending ---

/// <summary>Record or revoke the org's A2P/10DLC campaign attestation. Attestation-only (a deliberate trust statement,
/// not a provider check): while un-attested, the pre-send gate holds every SMS. Requires an SMS provider first.</summary>
public sealed record SetSmsA2pAttestationCommand(bool Attested, string? Reference)
    : ICommand, IAuthorizedRequest, IRequiresFeature
{
    public string RequiredFeature => PlanFeatures.NotificationsSms;

    public IReadOnlyCollection<string> RequiredPermissions => [Permissions.Notifications.Manage];
}

public sealed class SetSmsA2pAttestationCommandValidator : AbstractValidator<SetSmsA2pAttestationCommand>
{
    public SetSmsA2pAttestationCommandValidator() =>
        RuleFor(x => x.Reference).MaximumLength(NotificationChannelConfig.MaxA2pReferenceLength);
}

public sealed class SetSmsA2pAttestationCommandHandler(
    IApplicationDbContext dbContext, ICurrentUser currentUser, IDateTimeProvider clock, IAuditWriter audit)
    : ICommandHandler<SetSmsA2pAttestationCommand>
{
    public async Task<Result> Handle(SetSmsA2pAttestationCommand command, CancellationToken cancellationToken)
    {
        var config = await dbContext.NotificationChannelConfigs
            .FirstOrDefaultAsync(c => c.ChannelKind == ChannelKind.Sms, cancellationToken);
        if (config is null || config.CredentialCiphertext is null)
        {
            return Error.Validation("notification.sms_not_configured", "Configure an SMS provider before attesting your A2P campaign.");
        }

        if (command.Attested)
        {
            if (currentUser.UserId is not { } userId)
            {
                return Error.Unauthorized("auth.unauthenticated", "Authentication is required.");
            }

            config.AttestA2p(userId, command.Reference, clock.UtcNow);
        }
        else
        {
            config.ClearA2pAttestation();
        }

        audit.Record(command.Attested ? "notifications.sms_a2p_attested" : "notifications.sms_a2p_revoked",
            AuditOutcome.Success, nameof(NotificationChannelConfig), config.Id.ToString());

        return Result.Success();
    }
}

// --- Inbound webhook token lifecycle (W2) ---
//
// Gating is deliberately ASYMMETRIC. Minting a token is a feature action and is entitlement-gated. Ending a rotation
// and REVOKING are not: an operator whose module has lapsed, or whose token has leaked, must always be able to close
// an anonymous internet-facing endpoint. Gating the off-switch behind a subscription would be the wrong failure.

/// <summary>Mint a new inbound webhook token. Returns the plaintext ONCE — it is unrecoverable afterwards.</summary>
public sealed record MintSmsInboundWebhookTokenCommand(bool Rotate)
    : ICommand<SmsInboundWebhookTokenDto>, IAuthorizedRequest, IRequiresFeature
{
    public string RequiredFeature => PlanFeatures.NotificationsSms;

    public IReadOnlyCollection<string> RequiredPermissions => [Permissions.Notifications.Manage];
}

/// <summary>The show-once mint result. The plaintext token and the full URL appear here and nowhere else.</summary>
public sealed record SmsInboundWebhookTokenDto(string Token, string? InboundUrl, DateTimeOffset MintedAtUtc, DateTimeOffset? PreviousExpiresAtUtc);

public sealed class MintSmsInboundWebhookTokenCommandHandler(
    IApplicationDbContext dbContext, IDateTimeProvider clock, IAuditWriter audit, SmsNotificationOptions smsOptions)
    : ICommandHandler<MintSmsInboundWebhookTokenCommand, SmsInboundWebhookTokenDto>
{
    public async Task<Result<SmsInboundWebhookTokenDto>> Handle(
        MintSmsInboundWebhookTokenCommand command, CancellationToken cancellationToken)
    {
        var config = await dbContext.NotificationChannelConfigs
            .FirstOrDefaultAsync(c => c.ChannelKind == ChannelKind.Sms, cancellationToken);
        if (config is null)
        {
            return Error.Validation("notification.sms_not_configured", "Configure an SMS provider before minting an inbound webhook token.");
        }

        // Without a pinned public base URL the token is useless: we could show a URL, but no signature computed
        // against it could ever verify. Refuse loudly rather than hand over a token that cannot work.
        if (smsOptions.InboundWebhookBaseUrl is null)
        {
            return Error.Validation("notification.sms_inbound_endpoint_unavailable",
                "This installation has no public inbound webhook URL configured. Ask your administrator to set " +
                "Notifications:Sms:InboundWebhookBaseUrl to the public https origin of this install.");
        }

        var (token, hash) = SmsInboundToken.Generate();
        var now = clock.UtcNow;

        // Rotate keeps the old token alive through a grace window so repointing the provider cannot drop a STOP;
        // a plain mint (or a re-mint after a leak) discards it at once.
        config.MintInboundWebhookToken(
            hash,
            token[..NotificationChannelConfig.InboundWebhookTokenPrefixLength],
            now,
            grace: command.Rotate ? NotificationChannelConfig.InboundWebhookTokenGrace : null);

        audit.Record(command.Rotate ? "notifications.sms_inbound_token_rotated" : "notifications.sms_inbound_token_minted",
            AuditOutcome.Success, nameof(NotificationChannelConfig), config.Id.ToString());

        return new SmsInboundWebhookTokenDto(
            token,
            SmsInboundEndpoint.ComposeInboundUrl(smsOptions.InboundWebhookBaseUrl, token),
            now,
            config.PreviousInboundWebhookTokenExpiresAtUtc);
    }
}

/// <summary>End a rotation early — the operator confirms their provider is on the new URL. NOT entitlement-gated.</summary>
public sealed record CompleteSmsInboundWebhookTokenRotationCommand : ICommand, IAuthorizedRequest
{
    public IReadOnlyCollection<string> RequiredPermissions => [Permissions.Notifications.Manage];
}

public sealed class CompleteSmsInboundWebhookTokenRotationCommandHandler(
    IApplicationDbContext dbContext, IAuditWriter audit)
    : ICommandHandler<CompleteSmsInboundWebhookTokenRotationCommand>
{
    public async Task<Result> Handle(
        CompleteSmsInboundWebhookTokenRotationCommand command, CancellationToken cancellationToken)
    {
        var config = await dbContext.NotificationChannelConfigs
            .FirstOrDefaultAsync(c => c.ChannelKind == ChannelKind.Sms, cancellationToken);
        if (config is null)
        {
            return Error.NotFound("notification.sms_not_configured", "No SMS channel is configured.");
        }

        config.CompleteInboundWebhookTokenRotation();
        audit.Record("notifications.sms_inbound_token_rotation_completed", AuditOutcome.Success,
            nameof(NotificationChannelConfig), config.Id.ToString());
        return Result.Success();
    }
}

/// <summary>Revoke every inbound token — the leak response. Deliberately NOT entitlement-gated.</summary>
public sealed record ClearSmsInboundWebhookTokenCommand : ICommand, IAuthorizedRequest
{
    public IReadOnlyCollection<string> RequiredPermissions => [Permissions.Notifications.Manage];
}

public sealed class ClearSmsInboundWebhookTokenCommandHandler(IApplicationDbContext dbContext, IAuditWriter audit)
    : ICommandHandler<ClearSmsInboundWebhookTokenCommand>
{
    public async Task<Result> Handle(ClearSmsInboundWebhookTokenCommand command, CancellationToken cancellationToken)
    {
        var config = await dbContext.NotificationChannelConfigs
            .FirstOrDefaultAsync(c => c.ChannelKind == ChannelKind.Sms, cancellationToken);
        if (config is null)
        {
            return Error.NotFound("notification.sms_not_configured", "No SMS channel is configured.");
        }

        // Revoking also drops the readiness proof, so sending holds until a new token is minted AND demonstrated.
        // That coupling is the point: no inbound endpoint means no receivable STOP, so sending must stop too.
        config.ClearInboundWebhookToken();
        audit.Record("notifications.sms_inbound_token_revoked", AuditOutcome.Success,
            nameof(NotificationChannelConfig), config.Id.ToString());
        return Result.Success();
    }
}

// --- Read the SMS provider status (provider + configured + masked From + A2P; NEVER the credential) ---

/// <summary>The read surface for the SMS provider config — deliberately credential-free (docs/36 §5). A pure mapping so
/// the "credential never returned" property is unit-tested.</summary>
public sealed record SmsProviderStatusDto(
    string Provider, bool Configured, bool Enabled, string? FromMasked,
    bool A2pAttested, DateTimeOffset? A2pAttestedAtUtc, string? A2pReference,
    // Inbound webhook state (W2). Carries the token PREFIX and a token-FREE url prefix — never the token itself,
    // which is shown exactly once at mint and is unrecoverable afterwards.
    bool InboundTokenPresent, string? InboundTokenPrefix, DateTimeOffset? InboundTokenMintedAtUtc,
    string? InboundUrlPrefix, bool InboundEndpointConfigured,
    bool InboundReady, DateTimeOffset? InboundVerifiedAtUtc,
    bool InboundRotating, DateTimeOffset? InboundPreviousExpiresAtUtc, DateTimeOffset? InboundPreviousSeenAtUtc,
    int InboundRejectedCount, DateTimeOffset? InboundLastRejectedAtUtc);

public static class SmsProviderStatus
{
    public static SmsProviderStatusDto From(
        NotificationChannelConfig? config, string? normalizedInboundBaseUrl = null, DateTimeOffset? nowUtc = null) =>
        config is null
            ? new SmsProviderStatusDto(
                NotificationProviderKind.None.ToString(), false, false, null, false, null, null,
                false, null, null,
                SmsInboundEndpoint.ComposeInboundUrlPrefix(normalizedInboundBaseUrl),
                normalizedInboundBaseUrl is not null,
                false, null, false, null, null, 0, null)
            : new SmsProviderStatusDto(
                config.ProviderKind.ToString(),
                config.CredentialCiphertext != null,
                config.Enabled,
                config.FromValue is null ? null : MaskFrom(config.FromValue),
                config.A2pAttested,
                config.A2pAttestedAtUtc,
                config.A2pReference,
                config.HasInboundWebhookToken,
                config.InboundWebhookTokenPrefix,
                config.InboundWebhookTokenMintedAtUtc,
                SmsInboundEndpoint.ComposeInboundUrlPrefix(normalizedInboundBaseUrl),
                normalizedInboundBaseUrl is not null,
                config.InboundReady,
                config.InboundCurrentTokenVerifiedAtUtc,
                nowUtc is { } now && config.IsInboundTokenRotating(now),
                config.PreviousInboundWebhookTokenExpiresAtUtc,
                config.InboundPreviousTokenSeenAtUtc,
                config.InboundRejectedCount,
                config.InboundLastRejectedAtUtc);

    /// <summary>Mask the sender value for display — keep only the last 4 characters (a number or Messaging Service SID).</summary>
    public static string MaskFrom(string from) =>
        string.IsNullOrEmpty(from) || from.Length <= 4 ? "••••" : new string('•', from.Length - 4) + from[^4..];
}

public sealed record GetSmsProviderStatusQuery : IQuery<SmsProviderStatusDto>, IAuthorizedRequest
{
    public IReadOnlyCollection<string> RequiredPermissions => [Permissions.Notifications.Manage];
}

public sealed class GetSmsProviderStatusQueryHandler(
    IApplicationDbContext dbContext, IDateTimeProvider clock, SmsNotificationOptions smsOptions)
    : IQueryHandler<GetSmsProviderStatusQuery, SmsProviderStatusDto>
{
    public async Task<Result<SmsProviderStatusDto>> Handle(GetSmsProviderStatusQuery query, CancellationToken cancellationToken)
    {
        // Projected WITHOUT CredentialCiphertext so the encrypted secret is never even loaded (docs/36 §5). The token
        // hashes are likewise never selected — the DTO carries only the PREFIX and a token-free URL prefix.
        var config = await dbContext.NotificationChannelConfigs.AsNoTracking()
            .Where(c => c.ChannelKind == ChannelKind.Sms)
            .FirstOrDefaultAsync(cancellationToken);

        return SmsProviderStatus.From(config, smsOptions.InboundWebhookBaseUrl, clock.UtcNow);
    }
}

// --- Read the channel-config status (never the destination URL) ---

public sealed record NotificationChannelConfigDto(string Channel, string Provider, bool Configured, bool Enabled);

public sealed record ListNotificationChannelConfigsQuery : IQuery<IReadOnlyList<NotificationChannelConfigDto>>, IAuthorizedRequest
{
    public IReadOnlyCollection<string> RequiredPermissions => [Permissions.Notifications.Manage];
}

public sealed class ListNotificationChannelConfigsQueryHandler(IApplicationDbContext dbContext)
    : IQueryHandler<ListNotificationChannelConfigsQuery, IReadOnlyList<NotificationChannelConfigDto>>
{
    public async Task<Result<IReadOnlyList<NotificationChannelConfigDto>>> Handle(ListNotificationChannelConfigsQuery query, CancellationToken cancellationToken)
    {
        var configs = await dbContext.NotificationChannelConfigs
            .Select(c => new NotificationChannelConfigDto(
                c.ChannelKind.ToString(),
                c.ProviderKind.ToString(),
                c.DestinationUrl != null || c.CredentialCiphertext != null,
                c.Enabled))
            .ToListAsync(cancellationToken);

        return configs;
    }
}
