using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using WireHQ.Application.Abstractions.Webhooks;

namespace WireHQ.Infrastructure.Webhooks;

/// <summary>
/// POSTs a webhook delivery, signed with an <c>X-WireHQ-Signature: sha256=…</c> HMAC over the raw JSON body keyed by
/// the endpoint's secret, so a receiver can verify the payload's origin + integrity (docs/26-api-keys-webhooks.md
/// §8). A 2xx is success; anything else (or a transport error) is a failure the scheduler retries with backoff.
/// </summary>
public sealed class WebhookTransport(HttpClient httpClient, ILogger<WebhookTransport> logger) : IWebhookTransport
{
    public const string SignatureHeader = "X-WireHQ-Signature";
    public const string EventHeader = "X-WireHQ-Event";
    public const string DeliveryHeader = "X-WireHQ-Delivery";

    public async Task<WebhookSendResult> SendAsync(WebhookSendRequest request, CancellationToken cancellationToken)
    {
        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, request.Url);
            message.Content = new StringContent(request.PayloadJson, Encoding.UTF8);
            message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            message.Headers.TryAddWithoutValidation(SignatureHeader, "sha256=" + Sign(request.PayloadJson, request.SigningSecret));
            message.Headers.TryAddWithoutValidation(EventHeader, request.EventType);
            message.Headers.TryAddWithoutValidation(DeliveryHeader, request.DeliveryId.ToString());

            using var response = await httpClient.SendAsync(message, cancellationToken);
            var code = (int)response.StatusCode;
            return response.IsSuccessStatusCode
                ? new WebhookSendResult(true, code, null)
                : new WebhookSendResult(false, code, $"HTTP {code}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // TaskCanceledException here = the per-request timeout (the loop's own cancellation rethrows above).
            logger.LogDebug(ex, "Webhook delivery {DeliveryId} transport error.", request.DeliveryId);
            return new WebhookSendResult(false, null, ex is TaskCanceledException ? "Timed out" : ex.Message);
        }
    }

    private static string Sign(string payload, string secret)
    {
        var key = Encoding.UTF8.GetBytes(secret);
        var hash = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexStringLower(hash);
    }
}
