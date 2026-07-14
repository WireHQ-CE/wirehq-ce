using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WireHQ.Api.Controllers;
using WireHQ.Application.Features.Notifications;
using WireHQ.Domain.Notifications;

namespace WireHQ.Api.Controllers.V1;

/// <summary>
/// Notification rules for the active organization (docs/35-notifications.md §4.4). A rule delivers on matching audit
/// events via a channel to an audience. Wave 1 ships the free-core <b>Email</b> channel; Chat/SMS follow. Org-scoped
/// and RBAC-gated (<c>notifications.manage</c> — a sensitive permission). Kept-core (usable in every edition).
/// </summary>
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/notifications")]
[Authorize]
public sealed class NotificationsController : ApiControllerBase
{
    [HttpGet("rules")]
    public async Task<IActionResult> ListRules(CancellationToken cancellationToken) =>
        Ok(await Sender.Send(new ListNotificationRulesQuery(), cancellationToken));

    /// <summary>The curated event-type catalog for the rule editor.</summary>
    [HttpGet("event-types")]
    public async Task<IActionResult> EventTypes(CancellationToken cancellationToken) =>
        Ok(await Sender.Send(new ListNotificationEventTypesQuery(), cancellationToken));

    /// <summary>Recent delivery history for the organization.</summary>
    [HttpGet("deliveries")]
    public async Task<IActionResult> Deliveries([FromQuery] int pageSize = 50, CancellationToken cancellationToken = default) =>
        Ok(await Sender.Send(new ListNotificationDeliveriesQuery(pageSize), cancellationToken));

    /// <summary>The org's per-channel configuration status (never the destination URL).</summary>
    [HttpGet("channel-configs")]
    public async Task<IActionResult> ChannelConfigs(CancellationToken cancellationToken) =>
        Ok(await Sender.Send(new ListNotificationChannelConfigsQuery(), cancellationToken));

    /// <summary>Set the Chat (Teams/Slack) incoming-webhook destination for the organization.</summary>
    [HttpPut("channel-configs/chat")]
    public async Task<IActionResult> SetChatDestination(SetChatDestinationRequest request, CancellationToken cancellationToken) =>
        NoContent(await Sender.Send(new SetChatDestinationCommand(request.Provider, request.DestinationUrl), cancellationToken));

    [HttpPost("rules")]
    public async Task<IActionResult> Create(CreateNotificationRuleRequest request, CancellationToken cancellationToken) =>
        Created(await Sender.Send(
            new CreateNotificationRuleCommand(request.Name, request.EventPattern, request.ChannelKind, request.Audience, request.AudienceRef),
            cancellationToken));

    [HttpPut("rules/{id:guid}")]
    public async Task<IActionResult> Update(Guid id, UpdateNotificationRuleRequest request, CancellationToken cancellationToken) =>
        NoContent(await Sender.Send(
            new UpdateNotificationRuleCommand(id, request.Name, request.EventPattern, request.ChannelKind, request.Audience, request.AudienceRef),
            cancellationToken));

    [HttpPost("rules/{id:guid}/status")]
    public async Task<IActionResult> SetStatus(Guid id, SetNotificationRuleStatusRequest request, CancellationToken cancellationToken) =>
        NoContent(await Sender.Send(new SetNotificationRuleEnabledCommand(id, request.Enabled), cancellationToken));

    /// <summary>Send a sample of this rule to your own email — verifies the pipeline without fanning out.</summary>
    [HttpPost("rules/{id:guid}/test")]
    public async Task<IActionResult> Test(Guid id, CancellationToken cancellationToken) =>
        NoContent(await Sender.Send(new SendTestNotificationCommand(id), cancellationToken));

    [HttpDelete("rules/{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken) =>
        NoContent(await Sender.Send(new DeleteNotificationRuleCommand(id), cancellationToken));
}

public sealed record CreateNotificationRuleRequest(
    string Name, string EventPattern, ChannelKind ChannelKind, NotificationAudience Audience, Guid? AudienceRef);

public sealed record UpdateNotificationRuleRequest(
    string Name, string EventPattern, ChannelKind ChannelKind, NotificationAudience Audience, Guid? AudienceRef);

public sealed record SetNotificationRuleStatusRequest(bool Enabled);

public sealed record SetChatDestinationRequest(NotificationProviderKind Provider, string DestinationUrl);
