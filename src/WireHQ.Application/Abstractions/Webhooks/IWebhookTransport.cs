namespace WireHQ.Application.Abstractions.Webhooks;

/// <summary>
/// Sends a single webhook delivery over HTTP (docs/26-api-keys-webhooks.md §8). Implemented in the infrastructure
/// layer (an <c>HttpClient</c> that HMAC-signs the raw body with the endpoint's secret). The Application scheduler
/// unprotects the secret and hands the plaintext here; the port keeps HTTP + crypto out of the Application layer.
/// </summary>
public interface IWebhookTransport
{
    Task<WebhookSendResult> SendAsync(WebhookSendRequest request, CancellationToken cancellationToken);
}

/// <param name="Url">The endpoint URL to POST to.</param>
/// <param name="PayloadJson">The exact body to send (and sign).</param>
/// <param name="SigningSecret">The plaintext HMAC key (unprotected by the caller).</param>
/// <param name="DeliveryId">This delivery's id (sent as <c>X-WireHQ-Delivery</c>).</param>
/// <param name="EventType">The audit action name (sent as <c>X-WireHQ-Event</c>).</param>
public sealed record WebhookSendRequest(string Url, string PayloadJson, string SigningSecret, Guid DeliveryId, string EventType);

/// <param name="Success">True on a 2xx response.</param>
/// <param name="StatusCode">The HTTP status, or null on a transport error (DNS, timeout, connection).</param>
/// <param name="Error">A short failure reason when not successful.</param>
public sealed record WebhookSendResult(bool Success, int? StatusCode, string? Error);
