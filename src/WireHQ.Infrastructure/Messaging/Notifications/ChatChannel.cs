using System.Net.Http.Json;
using WireHQ.Application.Abstractions.Notifications;
using WireHQ.Application.Abstractions.Security;
using WireHQ.Domain.Notifications;

namespace WireHQ.Infrastructure.Messaging.Notifications;

/// <summary>
/// The <b>Chat Alerts</b> notification channel — Microsoft Teams / Slack (docs/35-notifications.md §4.3, Wave 2).
/// Formats the redacted summary for the provider (Slack text / Teams MessageCard) and POSTs it to the org's
/// incoming-webhook <c>DestinationUrl</c> through the <b>dedicated SSRF-guarded</b> HTTP client
/// (<see cref="HttpClientName"/>) — its own client re-declaring the webhook client's connect-time address guard +
/// no-redirects + short timeout, so an operator-supplied chat URL can't drive the server into internal / cloud-metadata
/// hosts (blocker B-7). Gated: only reached for a delivery whose rule required <c>notifications.chat</c>, which the
/// scheduler re-checks against the live entitlement union before dispatch (MM-14).
/// </summary>
public sealed class ChatChannel(IHttpClientFactory httpClientFactory, ISecretProtector secretProtector) : INotificationChannel
{
    public const string HttpClientName = "notifications-outbound";

    public ChannelKind Kind => ChannelKind.Chat;

    public async Task<ChannelSendResult> SendAsync(ChannelSendRequest request, CancellationToken cancellationToken)
    {
        var config = request.Config;
        if (config is null || !config.Enabled || string.IsNullOrWhiteSpace(config.DestinationUrl))
        {
            return ChannelSendResult.Failed("No chat destination is configured for this organisation.");
        }

        string destinationUrl;
        try
        {
            destinationUrl = secretProtector.Unprotect(config.DestinationUrl); // stored encrypted at rest
        }
        catch
        {
            return ChannelSendResult.Failed("Chat destination could not be read.");
        }

        var payload = BuildPayload(config.ProviderKind, request.Subject, request.Body);
        var client = httpClientFactory.CreateClient(HttpClientName);

        try
        {
            using var response = await client.PostAsJsonAsync(destinationUrl, payload, cancellationToken);
            return response.IsSuccessStatusCode
                ? ChannelSendResult.Ok((int)response.StatusCode)
                : ChannelSendResult.Failed($"Chat endpoint returned HTTP {(int)response.StatusCode}.", (int)response.StatusCode);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The SSRF guard throws for a disallowed destination; transport errors also land here — both are retryable
            // delivery failures (backoff). Return a GENERIC message so the delivery history never carries the
            // destination host / URL detail (review nit).
            return ChannelSendResult.Failed("Chat endpoint unreachable.");
        }
    }

    private static object BuildPayload(NotificationProviderKind provider, string subject, string body) => provider switch
    {
        // Microsoft Teams incoming webhook (legacy Office 365 connector) — a MessageCard. A Dictionary is used so the
        // required `@type` / `@context` keys serialize correctly (anonymous objects can't carry `@`-prefixed names).
        NotificationProviderKind.Teams => new Dictionary<string, object>
        {
            ["@type"] = "MessageCard",
            ["@context"] = "http://schema.org/extensions",
            ["summary"] = subject,
            ["themeColor"] = "0F6FDE",
            ["title"] = subject,
            ["text"] = body,
        },
        // Slack incoming webhook — plain text with the subject bolded.
        _ => new { text = $"*{subject}*\n{body}" },
    };
}
