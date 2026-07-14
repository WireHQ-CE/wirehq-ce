using Microsoft.EntityFrameworkCore;
using WireHQ.Application.Abstractions.Persistence;
using WireHQ.Application.Authorization;
using WireHQ.Application.Common.Messaging;
using WireHQ.Domain.Notifications;
using WireHQ.Shared.Results;

namespace WireHQ.Application.Features.Notifications;

// Read the org's notification rules + delivery history + the curated event catalog (docs/35-notifications.md).
// All gated notifications.manage. Org-scoped by the ambient tenant filter.

// --- Rules ---

public sealed record NotificationRuleDto(
    Guid Id, string Name, string EventPattern, string Channel, string Audience, Guid? AudienceRef, bool Enabled, DateTimeOffset CreatedAtUtc);

public sealed record ListNotificationRulesQuery : IQuery<IReadOnlyList<NotificationRuleDto>>, IAuthorizedRequest
{
    public IReadOnlyCollection<string> RequiredPermissions => [Permissions.Notifications.Manage];
}

public sealed class ListNotificationRulesQueryHandler(IApplicationDbContext dbContext)
    : IQueryHandler<ListNotificationRulesQuery, IReadOnlyList<NotificationRuleDto>>
{
    public async Task<Result<IReadOnlyList<NotificationRuleDto>>> Handle(ListNotificationRulesQuery query, CancellationToken cancellationToken)
    {
        var rules = await dbContext.NotificationRules
            .OrderByDescending(r => r.Id)
            .Select(r => new NotificationRuleDto(
                r.Id, r.Name, r.EventPattern, r.ChannelKind.ToString(), r.Audience.ToString(), r.AudienceRef, r.Enabled, r.CreatedAtUtc))
            .ToListAsync(cancellationToken);

        return rules;
    }
}

// --- Delivery history ---

public sealed record NotificationDeliveryDto(
    Guid Id, string Channel, string Recipient, string Subject, string Status, int Attempts, string? LastError,
    DateTimeOffset CreatedAtUtc, DateTimeOffset? DeliveredAtUtc);

public sealed record ListNotificationDeliveriesQuery(int PageSize = 50) : IQuery<IReadOnlyList<NotificationDeliveryDto>>, IAuthorizedRequest
{
    public IReadOnlyCollection<string> RequiredPermissions => [Permissions.Notifications.Manage];
}

public sealed class ListNotificationDeliveriesQueryHandler(IApplicationDbContext dbContext)
    : IQueryHandler<ListNotificationDeliveriesQuery, IReadOnlyList<NotificationDeliveryDto>>
{
    public async Task<Result<IReadOnlyList<NotificationDeliveryDto>>> Handle(ListNotificationDeliveriesQuery query, CancellationToken cancellationToken)
    {
        var pageSize = Math.Clamp(query.PageSize, 1, 200);
        var deliveries = await dbContext.NotificationDeliveries
            .OrderByDescending(d => d.Id)
            .Take(pageSize)
            .Select(d => new NotificationDeliveryDto(
                d.Id, d.ChannelKind.ToString(), d.Recipient, d.RenderedSubject, d.Status.ToString(),
                d.Attempts, d.LastError, d.CreatedAtUtc, d.DeliveredAtUtc))
            .ToListAsync(cancellationToken);

        return deliveries;
    }
}

// --- Curated event catalog (the rule-editor picker) ---

public sealed record NotificationEventTypeDto(string Pattern, string Label, string Group);

public sealed record ListNotificationEventTypesQuery : IQuery<IReadOnlyList<NotificationEventTypeDto>>, IAuthorizedRequest
{
    public IReadOnlyCollection<string> RequiredPermissions => [Permissions.Notifications.Manage];
}

public sealed class ListNotificationEventTypesQueryHandler
    : IQueryHandler<ListNotificationEventTypesQuery, IReadOnlyList<NotificationEventTypeDto>>
{
    // The Wave 1 curated set: low-frequency security / directory / lifecycle events (docs/35 §4.6 — high-frequency
    // actions are deliberately excluded from the routable set to avoid a per-recipient storm). Display strings only —
    // the patterns are audit-action globs, safe to ship in the kept-core CE (no forbidden type-name substrings).
    private static readonly IReadOnlyList<NotificationEventTypeDto> Catalog =
    [
        new("mfa.*", "Multi-factor authentication changes", "Security"),
        new("identity.users.*", "User lifecycle (invited, updated, removed)", "Directory"),
        new("identity.roles.*", "Role changes", "Directory"),
        new("identity.sso.*", "Single sign-on configuration", "Directory"),
        new("identity.scim.*", "SCIM provisioning", "Directory"),
        new("identity.ldap.*", "LDAP / Active Directory sync", "Directory"),
        new("policy.access.*", "Access-policy changes", "Access"),
        new("organization.*", "Organization changes", "Organization"),
        new("api.keys.*", "API-key changes", "Integrations"),
        new("webhooks.*", "Webhook changes", "Integrations"),
    ];

    public Task<Result<IReadOnlyList<NotificationEventTypeDto>>> Handle(ListNotificationEventTypesQuery query, CancellationToken cancellationToken) =>
        Task.FromResult(Result.Success(Catalog));
}
