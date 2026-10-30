namespace WireHQ.Application.Features.Notifications;

/// <summary>
/// Operator knob for the SMS notification channel (docs/36 §7). Carries only the operator's <b>configured</b> daily
/// per-org cap; the send path clamps it to a finite floor + hard max (NEVER 0/uncapped) via
/// <see cref="NotificationSendPolicy.ResolveSmsDailyCap"/>. Bound once in Infrastructure DI from
/// <c>Notifications:Sms:DailyCapPerOrg</c> — the Application layer never reads <c>IConfiguration</c> directly (same
/// pattern as <see cref="Entitlements.EntitlementOptions"/> / <see cref="Authentication.RegistrationOptions"/>).
/// </summary>
public sealed class SmsNotificationOptions
{
    /// <summary>The operator-configured daily SMS cap per org (0/absent ⇒ the default). Clamped to a hard max at send time.</summary>
    public int DailyCapPerOrg { get; init; }

    /// <summary>
    /// The <b>dark-boundary</b> master switch for real SMS sends (docs/36 §2/§10 — "no external send before Slice D").
    /// <b>Default FALSE.</b> While off, EVERY provider send is skipped in CODE: the verification (OTP) path builds the
    /// challenge + applies cooldown/rate-limits/cap but does not call the provider (so no phone can verify ⇒ the SMS
    /// audience resolver stays empty ⇒ the channel is fully dark), and the dispatch scheduler terminally
    /// <c>Cancel</c>s any SMS delivery. Bound in Infrastructure DI from <c>Notifications:Sms:Enabled</c>. Slice D flips
    /// this to <c>true</c> once STOP/HELP suppression + the A2P attestation gate land.
    /// </summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// The <b>pinned public base URL</b> the inbound STOP/HELP webhook signature is verified against (Wave 4 Slice D,
    /// docs/36 §5) — e.g. <c>https://sms.wirehq.net</c>. The provider (Twilio) signs the exact URL it POSTs to; the edge
    /// strips <c>X-Forwarded-Host</c> (Program.cs), so a request-derived URL would be the internal nginx host and every
    /// HMAC would fail. The handler reconstructs the signed URL as this base + <c>Request.Path</c> (the token lives in
    /// the path, never a query string). Bound from <c>Notifications:Sms:InboundWebhookBaseUrl</c>; empty ⇒ inbound
    /// verification can never succeed (fail-closed), which is correct while the operator hasn't stood the endpoint up.
    /// </summary>
    public string? InboundWebhookBaseUrl { get; init; }
}
