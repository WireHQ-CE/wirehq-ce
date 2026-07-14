using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WireHQ.Api.Controllers;
using WireHQ.Application.Features.Webhooks;

namespace WireHQ.Api.Controllers.V1;

/// <summary>
/// Outbound webhook endpoints for the active organization (docs/26-api-keys-webhooks.md §8). An endpoint is a URL
/// WireHQ POSTs to (HMAC-signed) when a subscribed audit event happens. Org-scoped, RBAC-gated
/// (<c>api.keys.manage</c>) and Enterprise-gated (<c>api.keys</c>). Kept-core (usable in the CE, which defaults to
/// Enterprise).
/// </summary>
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/webhooks")]
[Authorize]
public sealed class WebhooksController : ApiControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken cancellationToken) =>
        Ok(await Sender.Send(new ListWebhooksQuery(), cancellationToken));

    /// <summary>The subscribable event-type catalog for the endpoint editor.</summary>
    [HttpGet("event-types")]
    public async Task<IActionResult> EventTypes(CancellationToken cancellationToken) =>
        Ok(await Sender.Send(new ListWebhookEventTypesQuery(), cancellationToken));

    /// <summary>Recent delivery history, optionally scoped to one endpoint.</summary>
    [HttpGet("deliveries")]
    public async Task<IActionResult> Deliveries([FromQuery] Guid? endpointId, CancellationToken cancellationToken) =>
        Ok(await Sender.Send(new ListWebhookDeliveriesQuery(endpointId), cancellationToken));

    /// <summary>Create an endpoint → returns the signing secret ONCE (only its ciphertext is stored).</summary>
    [HttpPost]
    public async Task<IActionResult> Create(CreateWebhookRequest request, CancellationToken cancellationToken) =>
        Created(await Sender.Send(new CreateWebhookCommand(request.Url, request.Description, request.EventTypes ?? []), cancellationToken));

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, UpdateWebhookRequest request, CancellationToken cancellationToken) =>
        NoContent(await Sender.Send(new UpdateWebhookCommand(id, request.Url, request.Description, request.EventTypes ?? []), cancellationToken));

    [HttpPost("{id:guid}/status")]
    public async Task<IActionResult> SetStatus(Guid id, SetWebhookStatusRequest request, CancellationToken cancellationToken) =>
        NoContent(await Sender.Send(new SetWebhookStatusCommand(id, request.Enabled), cancellationToken));

    /// <summary>Rotate the signing secret → returns the new secret ONCE.</summary>
    [HttpPost("{id:guid}/rotate-secret")]
    public async Task<IActionResult> RotateSecret(Guid id, CancellationToken cancellationToken) =>
        Ok(await Sender.Send(new RotateWebhookSecretCommand(id), cancellationToken));

    /// <summary>Enqueue a test event to the endpoint.</summary>
    [HttpPost("{id:guid}/test")]
    public async Task<IActionResult> Test(Guid id, CancellationToken cancellationToken) =>
        NoContent(await Sender.Send(new SendTestWebhookCommand(id), cancellationToken));

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken) =>
        NoContent(await Sender.Send(new DeleteWebhookCommand(id), cancellationToken));
}

public sealed record CreateWebhookRequest(string Url, string? Description, IReadOnlyList<string>? EventTypes);

public sealed record UpdateWebhookRequest(string Url, string? Description, IReadOnlyList<string>? EventTypes);

public sealed record SetWebhookStatusRequest(bool Enabled);
