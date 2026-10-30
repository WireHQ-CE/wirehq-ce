using WireHQ.Application.Abstractions.Notifications;
using WireHQ.Domain.Notifications;

namespace WireHQ.Application.Features.Notifications;

/// <summary>
/// Pure, DB-free send-path policy for the dispatch scheduler (docs/36 §7): the finite-floor SMS daily-cap clamp and the
/// mapping from a channel's <see cref="ChannelSendResult"/> onto its delivery's terminal/retry state. Extracted so the
/// two spend-safety rules the adversarial review flagged are unit-tested away from the DB-bound scheduler.
/// </summary>
internal static class NotificationSendPolicy
{
    /// <summary>Default per-org daily SMS cap when the operator hasn't configured one (docs/36 §2/§7).</summary>
    public const int DefaultSmsDailyCapPerOrg = 100;

    /// <summary>The hard ceiling an operator override can never exceed — bounds per-message spend on a paid channel
    /// (docs/36 §7). A configured value above this is clamped down; the cap is never uncapped.</summary>
    public const int HardMaxSmsDailyCapPerOrg = 2000;

    /// <summary>
    /// Clamp the configured SMS cap to a FINITE floor + hard max — absent/0/negative ⇒ the default, never uncapped
    /// (docs/36 §7). This is load-bearing: <see cref="NotificationChannelUsage.HasReached"/> treats a cap of 0 as
    /// "no cap", so a 0 must never reach it — the default floor is enforced here.
    /// </summary>
    public static int ResolveSmsDailyCap(int configured) =>
        configured <= 0 ? DefaultSmsDailyCapPerOrg : Math.Min(configured, HardMaxSmsDailyCapPerOrg);

    /// <summary>
    /// The <b>dark-boundary</b> send-gate for the dispatch scheduler (docs/36 §2/§10 — "no external send before Slice
    /// D"). While SMS sending is disabled, an SMS delivery is terminally <see cref="NotificationDelivery.Cancel"/>led
    /// rather than dispatched — belt-and-braces defence in depth on top of the empty audience resolver + the OTP
    /// send-gate. Returns true when it gated the delivery (so the caller skips the send). Non-SMS channels + the
    /// enabled state pass straight through.
    /// </summary>
    public static bool CancelIfSmsSendingDisabled(NotificationDelivery delivery, bool smsSendingEnabled)
    {
        if (delivery.ChannelKind == ChannelKind.Sms && !smsSendingEnabled)
        {
            delivery.Cancel("SMS sending is not yet enabled for this organisation");
            return true;
        }

        return false;
    }

    /// <summary>
    /// The <b>A2P/10DLC pre-send gate</b> (docs/36 §4.6, Slice D): no SMS may be sent until the operator has attested
    /// their campaign is registered. An un-attested org's SMS delivery is <b>held as a transient failure</b> (retries on
    /// the backoff ladder) — NOT a terminal <c>Cancel</c> — so an alert queued just before the operator attests still
    /// goes out once they do; it ages out on the normal retry cap otherwise. Returns true when it held the delivery (so
    /// the caller skips the send). Non-SMS + an attested config pass straight through.
    /// </summary>
    public static bool HoldIfA2pNotAttested(NotificationDelivery delivery, NotificationChannelConfig? config, DateTimeOffset nowUtc)
    {
        if (delivery.ChannelKind != ChannelKind.Sms || config?.A2pAttested == true)
        {
            return false;
        }

        delivery.MarkFailed(null, "SMS A2P campaign is not attested for this organisation", nowUtc);
        return true;
    }

    /// <summary>
    /// Hold SMS for an org whose inbound webhook has not been demonstrated to work — the machine-checked half of
    /// "we respect STOP".
    ///
    /// The bar is a token that has actually CARRIED a correctly-signed request, not merely a minted one: an
    /// unconnected webhook is indistinguishable, from the subscriber's side, from having no webhook at all, and
    /// sending under it means messaging people who have no working way to stop us. That is the single failure the
    /// whole compliance design exists to prevent, so it is enforced here rather than left to a checklist.
    ///
    /// Transient, like the A2P hold: the delivery retries on the normal ladder, so an alert queued moments before the
    /// operator's first STOP test still goes out once the proof lands, and ages out otherwise.
    /// </summary>
    public static bool HoldIfInboundNotReady(NotificationDelivery delivery, NotificationChannelConfig? config, DateTimeOffset nowUtc)
    {
        if (delivery.ChannelKind != ChannelKind.Sms || config?.InboundReady == true)
        {
            return false;
        }

        delivery.MarkFailed(null, "SMS inbound webhook is not verified for this organisation", nowUtc);
        return true;
    }

    /// <summary>
    /// Apply a channel send result to its delivery + the day's usage counter:
    /// <list type="bullet">
    /// <item>success ⇒ <see cref="NotificationDelivery.MarkSucceeded"/> + count one against the day's budget;</item>
    /// <item>a terminal <see cref="ChannelSendResult.IsSuppressed"/> (opt-out / Twilio 21610) ⇒
    /// <see cref="NotificationDelivery.Cancel"/> — <b>not</b> <see cref="NotificationDelivery.MarkFailed"/>, which would
    /// re-hammer an opted-out number 6× on the backoff ladder (docs/36 §7);</item>
    /// <item>anything else ⇒ a retryable <see cref="NotificationDelivery.MarkFailed"/> (backoff).</item>
    /// </list>
    /// </summary>
    public static void ApplyResult(NotificationDelivery delivery, ChannelSendResult result, NotificationChannelUsage usage, DateTimeOffset nowUtc)
    {
        if (result.Success)
        {
            delivery.MarkSucceeded(result.StatusCode, nowUtc, result.ProviderMessageId);
            usage.Increment();
        }
        else if (result.IsSuppressed)
        {
            delivery.Cancel(result.Error ?? "Recipient has opted out of SMS.");
        }
        else if (result.IsIndeterminate)
        {
            // Terminal-unknown (Wave 4 W4): handed over, outcome never learned, deliberately NOT retried — a blind
            // resend double-charges the operator and texts a real person twice. The provider's status callback may
            // resolve it later.
            delivery.MarkUnresolved(
                result.Error ?? "The message was sent but the provider did not confirm it.", result.ProviderMessageId);

            // Count it against the daily cap. It probably cost money: the request reached the provider, and the whole
            // premise of not retrying is that it may well have been delivered. Not counting it would let a run of
            // unconfirmed sends quietly spend past a cap whose entire job is to bound the operator's carrier bill.
            usage.Increment();
        }
        else
        {
            delivery.MarkFailed(result.StatusCode, result.Error, nowUtc);
        }
    }
}
