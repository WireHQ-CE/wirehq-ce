using System.Security.Cryptography;

namespace WireHQ.Application.Features.Notifications;

/// <summary>The compliance action an inbound SMS keyword maps to (docs/36 §4.3, Wave 4 Slice D).</summary>
public enum SmsInboundAction
{
    /// <summary>Not a recognised keyword — no ledger change, no reply.</summary>
    None,

    /// <summary>A STOP-family keyword — suppress the number (append the ledger) + send an opt-out confirmation.</summary>
    Stop,

    /// <summary>A START-family keyword — clear the suppression (a state transition) + send a resubscribe confirmation.</summary>
    Start,

    /// <summary>A HELP-family keyword — no ledger change; send the HELP reply so the legal HELP obligation is met.</summary>
    Help,
}

/// <summary>
/// Classifies an inbound SMS body into its compliance <see cref="SmsInboundAction"/> (docs/36 §4.3). Uses the standard
/// carrier keyword families; matching is case-insensitive on the first whitespace-delimited token so trailing text
/// ("STOP please") still opts out. WireHQ keeps its own STOP/HELP handling authoritative rather than relying on the
/// provider's auto-responder, so the same keyword sets work across Twilio/Vonage (docs/36 §4.3).
/// </summary>
public static class SmsInboundKeywords
{
    private static readonly HashSet<string> StopWords =
        new(StringComparer.OrdinalIgnoreCase) { "STOP", "STOPALL", "UNSUBSCRIBE", "CANCEL", "END", "QUIT", "REVOKE", "OPTOUT" };

    private static readonly HashSet<string> StartWords =
        new(StringComparer.OrdinalIgnoreCase) { "START", "YES", "UNSTOP", "OPTIN" };

    private static readonly HashSet<string> HelpWords =
        new(StringComparer.OrdinalIgnoreCase) { "HELP", "INFO" };

    public static SmsInboundAction Classify(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return SmsInboundAction.None;
        }

        // The first token, uppercased — the standard carrier keyword position.
        var token = body.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[0];

        if (StopWords.Contains(token))
        {
            return SmsInboundAction.Stop;
        }

        if (StartWords.Contains(token))
        {
            return SmsInboundAction.Start;
        }

        return HelpWords.Contains(token) ? SmsInboundAction.Help : SmsInboundAction.None;
    }
}

/// <summary>
/// The org's <b>inbound webhook token</b> (docs/36 §5, Wave 4 Slice D). A high-entropy random token lives in the
/// operator's provider-configured inbound URL path; only its SHA-256 hash is stored (on the channel config's
/// <c>InboundWebhookTokenHash</c>), so the webhook resolves the org by <b>hashing the presented token and matching the
/// hash</b> — never a plaintext compare and never a credential decrypt first. The
/// plaintext is shown to the operator once at mint time and is unrecoverable thereafter (rotate to get a new one).
/// </summary>
public static class SmsInboundToken
{
    /// <summary>SHA-256 hex of the token — the stored + looked-up form. Deterministic so the lookup is a single indexed
    /// equality on a 256-bit-derived digest (not a per-byte plaintext comparison an attacker could time).</summary>
    public static string Hash(string token) =>
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)));

    /// <summary>Mint a fresh token: 32 random bytes, URL-safe base64 (no padding), returned with its stored hash. The
    /// plaintext is shown to the operator exactly once.</summary>
    public static (string Token, string Hash) Generate()
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
        return (token, Hash(token));
    }
}

/// <summary>
/// Composes the inbound-webhook URL — the one the operator pastes into their provider, and the one the signature is
/// verified against. Both come from here on purpose: the provider signs the URL it posts to, so if the displayed URL
/// and the reconstructed URL are built by different code they can drift into an HMAC that never matches, and every
/// inbound STOP is silently dropped while the console shows a healthy-looking configuration (docs/36 §5).
/// </summary>
public static class SmsInboundEndpoint
{
    public const string InboundPathPrefix = "/api/v1/sms/inbound/";

    /// <summary>
    /// Accept only an absolute <b>https</b> origin with no path, query or fragment, and return just the authority.
    ///
    /// A base carrying a path prefix is the trap this exists to close: it would compose a URL that reconstructs
    /// differently from the arriving <c>Request.Path</c>, producing a signature that can never match. Returning null
    /// for anything invalid is deliberate — the caller must fail closed rather than fall back to some other origin.
    /// </summary>
    public static string? Normalize(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            return null;
        }

        if (!Uri.TryCreate(configured.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || uri.AbsolutePath != "/"
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            return null;
        }

        return uri.GetLeftPart(UriPartial.Authority);
    }

    /// <summary>The operator's paste-ready URL. Null when no base is configured — there is nothing honest to show.</summary>
    public static string? ComposeInboundUrl(string? normalizedBaseUrl, string token) =>
        normalizedBaseUrl is null ? null : $"{normalizedBaseUrl}{InboundPathPrefix}{token}";

    /// <summary>The token-free prefix, safe to return from a status DTO that must never carry the secret.</summary>
    public static string? ComposeInboundUrlPrefix(string? normalizedBaseUrl) =>
        normalizedBaseUrl is null ? null : $"{normalizedBaseUrl}{InboundPathPrefix}";

    /// <summary>Reconstruct the exact URL the provider signed: pinned base + the arriving path.</summary>
    public static string ComposeVerificationUrl(string normalizedBaseUrl, string requestPath) =>
        $"{normalizedBaseUrl}{requestPath}";
}

/// <summary>
/// Composes the <b>delivery status callback</b> URL — the one handed to the provider on each send, and the one its
/// signature is verified against (docs/36 §7, Wave 4 W4). Same one-composer discipline as
/// <see cref="SmsInboundEndpoint"/>, and for the same reason: the provider signs the URL it posts to, so a displayed
/// URL and a reconstructed URL built by different code drift into an HMAC that never matches — and the symptom is
/// silence, not an error.
/// <para>
/// <b>Why the delivery id and not the inbound token.</b> The inbound webhook carries a secret token because a
/// subscriber's text is otherwise unattributable — there is nothing else in it that names an organisation. A status
/// callback is different: it concerns a message <i>we</i> sent, so the delivery id names the org by itself. And the
/// token could not be used here even if it were wanted: only its SHA-256 hash is stored (W2), so no code path after
/// mint can reconstruct the plaintext to put in a URL. Authenticity therefore rests entirely on the provider signature,
/// verified with that delivery's own organisation's credential — so a caller who knows a delivery id but not the
/// operator's auth token can do nothing at all.
/// </para>
/// </summary>
public static class SmsStatusEndpoint
{
    public const string StatusPathPrefix = "/api/v1/sms/status/";

    /// <summary>The per-message callback URL. Null when no public base is pinned — the send then simply carries no
    /// callback, and a lost response stays unresolved rather than being resolved later.</summary>
    public static string? ComposeStatusUrl(string? normalizedBaseUrl, Guid deliveryId) =>
        normalizedBaseUrl is null || deliveryId == Guid.Empty
            ? null
            : $"{normalizedBaseUrl}{StatusPathPrefix}{deliveryId}";

    /// <summary>Reconstruct the exact URL the provider signed: pinned base + the arriving path.</summary>
    public static string ComposeVerificationUrl(string normalizedBaseUrl, string requestPath) =>
        $"{normalizedBaseUrl}{requestPath}";
}
