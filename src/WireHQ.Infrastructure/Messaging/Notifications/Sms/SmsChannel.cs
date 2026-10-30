using WireHQ.Application.Abstractions.Notifications;
using WireHQ.Application.Abstractions.Security;
using WireHQ.Application.Features.Notifications;
using WireHQ.Domain.Notifications;

namespace WireHQ.Infrastructure.Messaging.Notifications.Sms;

/// <summary>
/// The <b>SMS</b> notification channel (docs/36 §3, Wave 4) — a bring-your-own-provider adapter that clones
/// <see cref="ChatChannel"/>: it reads the per-org <see cref="NotificationChannelConfig"/>, <c>ISecretProtector</c>-
/// unprotects the provider credential, and delegates the actual REST call to an <see cref="ISmsProvider"/>. Gated: only
/// reached for a delivery whose rule required <c>notifications.sms</c>, re-checked against the live entitlement union
/// before dispatch (MM-14). Slice A ships the engine <b>dark</b> — the scheduler's phone-audience resolver returns zero
/// recipients, so this channel produces no live sends until Slice B.
/// <para>Security posture (docs/36 §5): the credential is never logged; the provider already collapses transport errors
/// to a fixed generic string, and this channel adds a defence-in-depth catch so nothing (number/credential/body) can
/// reach the scheduler's <c>ex.Message</c> → delivery-history path. A provider opt-out (Twilio 21610) is surfaced as a
/// terminal <see cref="ChannelSendResult.Suppressed"/> so the scheduler <c>Cancel</c>s rather than retrying.</para>
/// </summary>
public sealed class SmsChannel(ISmsProvider smsProvider, ISecretProtector secretProtector, SmsNotificationOptions smsOptions)
    : INotificationChannel
{
    public ChannelKind Kind => ChannelKind.Sms;

    public async Task<ChannelSendResult> SendAsync(ChannelSendRequest request, CancellationToken cancellationToken)
    {
        var config = request.Config;
        if (config is null || !config.Enabled
            || string.IsNullOrWhiteSpace(config.CredentialCiphertext) || string.IsNullOrWhiteSpace(config.FromValue))
        {
            // Distinct from Chat's DestinationUrl guard: SMS needs the encrypted credential AND the sender number.
            return ChannelSendResult.Failed("No SMS provider is configured for this organisation.");
        }

        string credential;
        try
        {
            credential = secretProtector.Unprotect(config.CredentialCiphertext); // stored encrypted at rest
        }
        catch
        {
            return ChannelSendResult.Failed("SMS provider credential could not be read.");
        }

        try
        {
            // Disclosure on EVERY alert body (docs/36 §4.2, Slice D precondition D-1): prepend the sender identity and
            // append "Reply STOP to opt out", reserving that budget out of the single segment so the opt-out notice is
            // never crowded out by truncation. The rule-driven body arrives raw (RenderedBody); this is the single
            // chokepoint that turns it into a compliant, disclosure-bearing message.
            var body = SmsNotificationDisclosure.RenderAlertBody(request.Body, includeStopNotice: true);

            // Per-message status callback (Wave 4 W4): where the provider reports what finally happened to THIS
            // message. Composed by the one composer that also reconstructs the verification URL, so the URL we hand
            // over and the URL we later verify against cannot drift into an HMAC that never matches. Null when the
            // installation pins no public URL — the send still goes, it just can never be resolved afterwards.
            var statusCallbackUrl = SmsStatusEndpoint.ComposeStatusUrl(smsOptions.InboundWebhookBaseUrl, request.DeliveryId);

            var result = await smsProvider.SendAsync(
                new SmsSendRequest(request.Recipient, config.FromValue, body, credential, statusCallbackUrl),
                cancellationToken);

            if (result.Suppressed)
            {
                // Terminal opt-out (carrier 21610 / suppression) — the scheduler Cancels rather than retries (docs/36 §7).
                return ChannelSendResult.Suppressed(result.Error ?? "Recipient has opted out of SMS.");
            }

            if (result.Indeterminate)
            {
                // We handed it over and never learned the outcome. Terminal-unknown, NOT a retryable failure: resending
                // would charge twice and text a real person twice (docs/36 §7/§12).
                return ChannelSendResult.Indeterminate(
                    result.Error ?? "SMS was sent but the provider did not confirm it.", result.MessageSid);
            }

            return result.Success
                ? ChannelSendResult.Ok(providerMessageId: result.MessageSid)
                : ChannelSendResult.Failed(result.Error ?? "SMS could not be sent.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Defence in depth: the provider already catches internally, but never let a number/credential/body reach
            // the scheduler's ex.Message → delivery history (docs/36 §5). Fixed generic error.
            //
            // ISmsProvider is a seam (Vonage drops in behind it), and since the provider collapses every NON-OCE to a
            // failed result, an OCE is the only thing that can actually reach here — so without the second clause this
            // catch is dead code, and a provider timeout leaks straight past the chokepoint it exists to be.
            //
            // INDETERMINATE rather than Failed (Wave 4 W4): what reaches here is a timeout out of a seam we cannot see
            // inside. The request may well have been delivered to the provider, so this is precisely the case where a
            // retry is the dangerous move, not the safe one.
            return ChannelSendResult.Indeterminate("SMS was sent but the provider did not confirm it.");
        }
    }
}
