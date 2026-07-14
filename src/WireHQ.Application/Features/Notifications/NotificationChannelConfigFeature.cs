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
