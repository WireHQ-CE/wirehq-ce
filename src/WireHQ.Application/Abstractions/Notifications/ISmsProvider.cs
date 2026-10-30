using WireHQ.Domain.Notifications;

namespace WireHQ.Application.Abstractions.Notifications;

/// <summary>
/// The provider-specific SMS seam (docs/36 §3). One adapter per provider lives behind this port in Infrastructure
/// (Twilio first; Vonage is an additive drop-in in Slice E), so <c>SmsChannel</c> + the inbound webhook stay
/// provider-agnostic and each provider owns its own REST shape, host allow-list, and inbound-signature algorithm.
/// <see cref="SendAsync"/> is outbound; <see cref="VerifyAndParse"/> (Slice D) is the inbound STOP/HELP webhook's
/// provider-specific signature verification + parse.
/// </summary>
public interface ISmsProvider
{
    /// <summary>Which <see cref="NotificationProviderKind"/> this adapter speaks for (Twilio in Slice A).</summary>
    NotificationProviderKind Provider { get; }

    /// <summary>Send one message via the operator's own provider account. Never throws for a transport/provider error —
    /// it returns a <see cref="SmsSendResult"/> whose <see cref="SmsSendResult.Error"/> is a FIXED generic string, so no
    /// number/credential/body can leak into the delivery history (docs/36 §5).</summary>
    Task<SmsSendResult> SendAsync(SmsSendRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Verify an inbound provider webhook's signature and parse it into a normalized <see cref="InboundSmsMessage"/>
    /// (docs/36 §5, Slice D). <b>Fail-closed</b>: returns <c>null</c> whenever the signature can't be verified (the
    /// caller then drops the request with a 204 + audit — never a 4xx/5xx oracle or retry-storm). The signature is
    /// computed against the caller-supplied <b>pinned public URL</b> (never <c>Request.Host</c>), using the operator's
    /// own decrypted credential — the org has already been resolved from the inbound token, so no credential is touched
    /// before that. Each provider owns its real algorithm (Twilio = HMAC-SHA1 over URL + alphabetically-sorted form
    /// params, compared with <see cref="System.Security.Cryptography.CryptographicOperations.FixedTimeEquals"/>).
    /// </summary>
    InboundSmsMessage? VerifyAndParse(SmsInboundRequest request);

    /// <summary>
    /// Verify a provider webhook's signature <b>only</b>, without requiring it to be a parseable inbound message
    /// (Wave 4 W4). The delivery status callback is signed by exactly the same scheme as the inbound webhook but is a
    /// different kind of payload, so running it through <see cref="VerifyAndParse"/> would make its authenticity
    /// depend on fields the parse happens to require — and a missing field would then look identical to a forged
    /// signature. Two very different failures must not share one answer. Fail-closed: false on any error.
    /// </summary>
    bool VerifySignature(SmsInboundRequest request);
}

/// <summary>One inbound provider webhook to verify + parse. <see cref="Url"/> is the PINNED public webhook URL (config,
/// not request-derived); <see cref="Form"/> is the provider's POST form; <see cref="Signature"/> is the provider's
/// signature header; <see cref="CredentialPlaintext"/> is the already-decrypted operator credential
/// (<c>AccountSid:AuthToken</c> for Twilio) whose token is the HMAC key. Resolved AFTER the org is matched by token.</summary>
public sealed record SmsInboundRequest(string Url, IReadOnlyDictionary<string, string> Form, string Signature, string CredentialPlaintext);

/// <summary>A verified inbound SMS, normalized across providers. <see cref="FromE164"/> is the sender (the number that
/// texted STOP/START/HELP); <see cref="ToValue"/> is the org's sender it was sent to; <see cref="Body"/> is the raw
/// message text (the keyword is classified downstream).</summary>
public sealed record InboundSmsMessage(string FromE164, string ToValue, string Body);

/// <summary>One outbound SMS to send. <see cref="CredentialPlaintext"/> is the already-decrypted provider credential
/// (the channel unprotects the stored ciphertext before calling); it never appears in a query or a log.
/// <see cref="StatusCallbackUrl"/> is where the provider should report the message's final status (Wave 4 W4) — null
/// when the installation has no pinned public URL, in which case a send that times out simply stays unresolved
/// forever rather than being resolved later.</summary>
public sealed record SmsSendRequest(
    string ToPhoneE164, string FromValue, string Body, string CredentialPlaintext, string? StatusCallbackUrl = null);

/// <summary>
/// The outcome of a provider send. <see cref="Suppressed"/> is a TERMINAL non-retryable signal — the recipient/carrier
/// opted out (Twilio error 21610) — which the channel maps to a suppressed channel result so the scheduler
/// <c>Cancel</c>s rather than retries (docs/36 §7). <see cref="MessageSid"/> is the provider message id, carried for the
/// app-side idempotency marker (persisting it across attempts is Slice D — the seam returns it now). <see cref="Error"/>
/// is always a fixed generic string on failure — never the raw provider body (docs/36 §5).
/// </summary>
public sealed record SmsSendResult(
    bool Success, string? MessageSid = null, bool Suppressed = false, string? Error = null, bool Indeterminate = false)
{
    public static SmsSendResult Ok(string? messageSid) => new(true, messageSid);

    /// <summary>The carrier/recipient has opted out (Twilio 21610) — terminal, do not retry (docs/36 §7).</summary>
    public static SmsSendResult OptedOut(string reason) => new(false, Suppressed: true, Error: reason);

    /// <summary>
    /// A <b>definite</b> failure: the provider answered and refused. Nothing was queued, so retrying is safe and free.
    /// </summary>
    public static SmsSendResult Fail(string error) => new(false, Error: error);

    /// <summary>
    /// The request left the process and the outcome was never learned — a timeout, a dropped connection, a 5xx (Wave 4
    /// W4). Distinguished from <see cref="Fail"/> because the two need OPPOSITE handling: a definite refusal is safe to
    /// retry, while retrying this one risks a second charge and a second text to a real person. The scheduler marks it
    /// terminal-unresolved instead, and the provider's status callback may resolve it later.
    /// </summary>
    public static SmsSendResult Unknown(string error) => new(false, Error: error, Indeterminate: true);
}
