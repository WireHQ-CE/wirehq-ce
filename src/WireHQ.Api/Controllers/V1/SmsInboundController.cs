using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WireHQ.Api.Controllers;
using WireHQ.Application.Features.Notifications;

namespace WireHQ.Api.Controllers.V1;

/// <summary>
/// The inbound SMS provider webhook (docs/36 §5, Wave 4 Slice D) — where a subscriber's STOP/START/HELP text lands so
/// WireHQ's suppression ledger stays authoritative. Anonymous + provider-signature-verified: the raw form must not be
/// model-bound, the org is resolved from the token in the URL <b>path</b> (never a query string, so the signed URL is
/// exactly the pinned base + <see cref="HttpRequest.Path"/>), and the handler is fail-closed + no-oracle — <b>every</b>
/// outcome (unknown token, bad signature, processed) returns <c>204</c> with no body, so a probe learns nothing and a
/// provider retry storm is impossible. Borrows only the generic raw-body/signature approach — it deliberately shares no
/// code with the billing/Stripe webhook surface.
/// </summary>
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/sms")]
public sealed class SmsInboundController : ApiControllerBase
{
    [HttpPost("inbound/{token}")]
    [AllowAnonymous]
    public async Task<IActionResult> Inbound(string token, CancellationToken cancellationToken)
    {
        // Read the provider's form params (Twilio POSTs application/x-www-form-urlencoded). Ordinal keys — the provider
        // signs them sorted by key. A missing/duplicate key is harmless: the signature check is authoritative.
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
        if (Request.HasFormContentType)
        {
            var form = await Request.ReadFormAsync(cancellationToken);
            foreach (var field in form)
            {
                parameters[field.Key] = field.Value.ToString();
            }
        }

        var signature = Request.Headers["X-Twilio-Signature"].ToString();

        // Always 204 — the handler is fail-closed/no-oracle and never returns a domain error for an inbound webhook.
        return NoContent(await Sender.Send(
            new ProcessInboundSmsCommand(token, Request.Path.ToString(), parameters, signature), cancellationToken));
    }

    /// <summary>
    /// The provider's <b>delivery status callback</b> (docs/36 §7, Wave 4 W4) — how a send whose response we lost is
    /// resolved to what actually happened. Same posture as the inbound webhook above: anonymous, signature-verified
    /// against the pinned public URL, and <b>always 204</b>.
    /// <para>The path carries the delivery id rather than a secret token, and deliberately so: only the token's hash is
    /// stored (W2), so no code path could put the plaintext in a URL at send time. The delivery id names the
    /// organisation; authenticity rests wholly on the provider signature, verified with that organisation's own
    /// credential. See <c>SmsStatusEndpoint</c>.</para>
    /// </summary>
    [HttpPost("status/{deliveryId:guid}")]
    [AllowAnonymous]
    public async Task<IActionResult> Status(Guid deliveryId, CancellationToken cancellationToken)
    {
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
        if (Request.HasFormContentType)
        {
            var form = await Request.ReadFormAsync(cancellationToken);
            foreach (var field in form)
            {
                parameters[field.Key] = field.Value.ToString();
            }
        }

        var signature = Request.Headers["X-Twilio-Signature"].ToString();

        return NoContent(await Sender.Send(
            new ProcessSmsStatusCallbackCommand(deliveryId, Request.Path.ToString(), parameters, signature),
            cancellationToken));
    }
}
