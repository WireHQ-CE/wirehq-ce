using WireHQ.Application.Authorization;
using WireHQ.Application.Common.Messaging;
using WireHQ.Application.Entitlements;
using WireHQ.Shared.Results;

namespace WireHQ.Application.Features.Webhooks;

/// <summary>
/// A curated catalog of subscribable event patterns for the endpoint editor (docs/26-api-keys-webhooks.md §7). These
/// are audit-action globs (<c>prefix.*</c>) grouped for the picker; an endpoint may also subscribe to any exact
/// action name a customer knows. Only <b>org-scoped</b> events are delivered (platform actions are never sent).
/// Enterprise-gated (api.keys) + api.keys.manage.
/// </summary>
public sealed record ListWebhookEventTypesQuery : IQuery<IReadOnlyList<WebhookEventTypeOption>>, IAuthorizedRequest, IRequiresFeature
{
    public IReadOnlyCollection<string> RequiredPermissions => [Permissions.ApiKeys.Manage];

    public string RequiredFeature => PlanFeatures.ApiKeys;
}

public sealed record WebhookEventTypeOption(string Pattern, string Group, string Description);

public sealed class ListWebhookEventTypesQueryHandler : IQueryHandler<ListWebhookEventTypesQuery, IReadOnlyList<WebhookEventTypeOption>>
{
    private static readonly IReadOnlyList<WebhookEventTypeOption> Catalog =
    [
        new("*", "All", "Every event in your organization"),
        new("identity.users.*", "Users", "User invited, updated, or removed"),
        new("identity.roles.*", "Roles", "Custom roles created, updated, or deleted"),
        new("identity.teams.*", "Teams", "Teams created, updated, or deleted"),
        new("identity.sso.*", "Identity", "Single sign-on connection changes"),
        new("identity.scim.*", "Identity", "SCIM provisioning events"),
        new("identity.ldap.*", "Identity", "Directory (LDAP/AD) connection & sync events"),
        new("policy.access.*", "Access Policies", "Access policies compiled or applied"),
        new("wg.*", "WireGuard", "Instances, peers, and deployments"),
        new("api.keys.*", "API keys", "API keys created or revoked"),
        new("webhooks.*", "Webhooks", "Webhook endpoint & secret changes"),
        new("organization.*", "Organization", "Organization profile & settings changes"),
        new("mfa.*", "Security", "Multi-factor authentication enabled/disabled"),
        new("onboarding.*", "Organization", "Onboarding completed or skipped"),
    ];

    public Task<Result<IReadOnlyList<WebhookEventTypeOption>>> Handle(ListWebhookEventTypesQuery query, CancellationToken cancellationToken) =>
        Task.FromResult(Result.Success(Catalog));
}
