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

    /// <summary>The org's SMS provider status (provider + configured + masked From; NEVER the credential — docs/36 §5).</summary>
    [HttpGet("channel-configs/sms")]
    public async Task<IActionResult> SmsProviderStatus(CancellationToken cancellationToken) =>
        Ok(await Sender.Send(new GetSmsProviderStatusQuery(), cancellationToken));

    /// <summary>Set the org's own SMS provider credential + sender (write-only credential; docs/36 §5). Gated on notifications.sms.</summary>
    [HttpPut("channel-configs/sms")]
    public async Task<IActionResult> SetSmsProvider(SetSmsProviderRequest request, CancellationToken cancellationToken) =>
        NoContent(await Sender.Send(
            new SetSmsProviderCommand(request.Provider, request.AccountSid, request.AuthToken, request.FromValue), cancellationToken));

    /// <summary>Record/revoke the org's A2P/10DLC campaign attestation (docs/36 §4.6) — the pre-send gate that unlocks
    /// SMS sending. Gated on notifications.sms.</summary>
    [HttpPut("channel-configs/sms/a2p")]
    public async Task<IActionResult> SetSmsA2pAttestation(SetSmsA2pAttestationRequest request, CancellationToken cancellationToken) =>
        NoContent(await Sender.Send(new SetSmsA2pAttestationCommand(request.Attested, request.Reference), cancellationToken));

    /// <summary>Mint (or rotate) the org's inbound STOP/HELP webhook token. The response carries the plaintext token
    /// and the paste-ready URL <b>once</b> — neither is recoverable afterwards. Gated on notifications.sms.</summary>
    [HttpPost("channel-configs/sms/inbound-token")]
    public async Task<IActionResult> MintSmsInboundWebhookToken(
        MintSmsInboundWebhookTokenRequest request, CancellationToken cancellationToken) =>
        Ok(await Sender.Send(new MintSmsInboundWebhookTokenCommand(request.Rotate), cancellationToken));

    /// <summary>End a token rotation early, once the provider is confirmed on the new URL. Deliberately NOT
    /// entitlement-gated — closing the old URL must not depend on a live subscription.</summary>
    [HttpPost("channel-configs/sms/inbound-token/complete-rotation")]
    public async Task<IActionResult> CompleteSmsInboundWebhookTokenRotation(CancellationToken cancellationToken) =>
        NoContent(await Sender.Send(new CompleteSmsInboundWebhookTokenRotationCommand(), cancellationToken));

    /// <summary>Revoke every inbound webhook token — the leak response. Deliberately NOT entitlement-gated: an operator
    /// must always be able to close an anonymous internet-facing endpoint. Sending holds until a new token is minted
    /// and demonstrated.</summary>
    [HttpDelete("channel-configs/sms/inbound-token")]
    public async Task<IActionResult> ClearSmsInboundWebhookToken(CancellationToken cancellationToken) =>
        NoContent(await Sender.Send(new ClearSmsInboundWebhookTokenCommand(), cancellationToken));

    /// <summary>The org's SMS suppression ledger — numbers masked, newest first, active suppressions first.
    /// <c>phone</c> is an EXACT lookup, never a partial match (docs/36 §4.3).
    /// <para>⚠ This route is load-bearing: <c>ServerSpanScrubber</c> drops <c>url.query</c> for
    /// <c>/api/v1/notifications/sms/opt-outs</c> by literal prefix, because <c>?phone=</c> is not on any OTel redaction
    /// allow-list. Move or rename it and searched numbers export verbatim to the collector, with nothing failing.</para></summary>
    [HttpGet("sms/opt-outs")]
    public async Task<IActionResult> SmsOptOuts(
        [FromQuery] string? phone = null, [FromQuery] int pageSize = 50, CancellationToken cancellationToken = default) =>
        Ok(await Sender.Send(new ListSmsOptOutsQuery(phone, pageSize), cancellationToken));

    /// <summary>Record an opt-out that reached the operator off-channel — by phone, email or ticket (FCC 47 CFR
    /// 64.1200(a)(10): revocation by any reasonable means is valid). The number travels in the BODY, not the query, so it
    /// never reaches a span or an access log. There is deliberately no inverse: a suppression is lifted only by the
    /// subscriber's own START through the signed inbound webhook.</summary>
    [HttpPost("sms/opt-outs")]
    public async Task<IActionResult> RecordSmsOptOut(RecordSmsOptOutRequest request, CancellationToken cancellationToken) =>
        Ok(await Sender.Send(new RecordSmsOptOutCommand(request.Phone, request.Reason), cancellationToken));

    [HttpPost("rules")]
    public async Task<IActionResult> Create(CreateNotificationRuleRequest request, CancellationToken cancellationToken) =>
        Created(await Sender.Send(
            new CreateNotificationRuleCommand(
                request.Name, request.EventPattern, request.ChannelKind, request.Audience, request.AudienceRef,
                request.AdditionalPatterns, request.DigestCadence,
                request.QuietHoursStart, request.QuietHoursEnd, request.QuietHoursTimeZone, request.EscalationSteps),
            cancellationToken));

    [HttpPut("rules/{id:guid}")]
    public async Task<IActionResult> Update(Guid id, UpdateNotificationRuleRequest request, CancellationToken cancellationToken) =>
        NoContent(await Sender.Send(
            new UpdateNotificationRuleCommand(
                id, request.Name, request.EventPattern, request.ChannelKind, request.Audience, request.AudienceRef,
                request.AdditionalPatterns, request.DigestCadence,
                request.QuietHoursStart, request.QuietHoursEnd, request.QuietHoursTimeZone, request.EscalationSteps),
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

    /// <summary>The org's active (unacknowledged) escalation alerts — for the acknowledge UI.</summary>
    [HttpGet("alerts/active")]
    public async Task<IActionResult> ActiveEscalations(CancellationToken cancellationToken) =>
        Ok(await Sender.Send(new ListActiveEscalationsQuery(), cancellationToken));

    /// <summary>Acknowledge an escalation alert — stops the chain. Gated on <c>notifications.acknowledge</c>.</summary>
    [HttpPost("alerts/{jobId:guid}/acknowledge")]
    public async Task<IActionResult> AcknowledgeAlert(Guid jobId, CancellationToken cancellationToken) =>
        NoContent(await Sender.Send(new AcknowledgeNotificationAlertCommand(jobId), cancellationToken));
}

public sealed record CreateNotificationRuleRequest(
    string Name, string EventPattern, ChannelKind ChannelKind, NotificationAudience Audience, Guid? AudienceRef,
    IReadOnlyCollection<string>? AdditionalPatterns = null, DigestCadence DigestCadence = DigestCadence.Immediate,
    TimeOnly? QuietHoursStart = null, TimeOnly? QuietHoursEnd = null, string? QuietHoursTimeZone = null,
    IReadOnlyCollection<EscalationStepSpec>? EscalationSteps = null);

public sealed record UpdateNotificationRuleRequest(
    string Name, string EventPattern, ChannelKind ChannelKind, NotificationAudience Audience, Guid? AudienceRef,
    IReadOnlyCollection<string>? AdditionalPatterns = null, DigestCadence DigestCadence = DigestCadence.Immediate,
    TimeOnly? QuietHoursStart = null, TimeOnly? QuietHoursEnd = null, string? QuietHoursTimeZone = null,
    IReadOnlyCollection<EscalationStepSpec>? EscalationSteps = null);

public sealed record SetNotificationRuleStatusRequest(bool Enabled);

public sealed record SetChatDestinationRequest(NotificationProviderKind Provider, string DestinationUrl);

public sealed record SetSmsProviderRequest(NotificationProviderKind Provider, string AccountSid, string AuthToken, string FromValue);

public sealed record SetSmsA2pAttestationRequest(bool Attested, string? Reference);

/// <summary><c>Rotate</c> keeps the existing token working through a grace window so repointing the provider cannot
/// drop a STOP; false replaces it immediately, which is the right behaviour for a suspected leak.</summary>
public sealed record MintSmsInboundWebhookTokenRequest(bool Rotate = false);

public sealed record RecordSmsOptOutRequest(string Phone, string? Reason);
