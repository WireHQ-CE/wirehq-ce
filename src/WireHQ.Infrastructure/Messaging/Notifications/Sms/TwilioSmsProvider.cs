using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WireHQ.Application.Abstractions.Notifications;
using WireHQ.Domain.Notifications;

namespace WireHQ.Infrastructure.Messaging.Notifications.Sms;

/// <summary>
/// The <b>Twilio</b> outbound SMS adapter (docs/36 §3/§5). Bring-your-own-provider: it POSTs to Twilio's classic
/// Messages API using the <b>operator's own</b> Account SID + Auth Token (Basic auth), through the shared SSRF-guarded
/// <see cref="ChatChannel.HttpClientName"/> client. Hard security posture:
/// <list type="bullet">
/// <item>The destination is <b>built from a hardcoded host</b> (<see cref="ApiHost"/>), https-only — there is no
/// free-form provider base URL, and a belt-and-braces check refuses to attach credentials to any other host (SSRF +
/// credential-exfil guard, docs/36 §5).</item>
/// <item>Twilio error <b>21610</b> (recipient/carrier opted out) maps to a TERMINAL <see cref="SmsSendResult.Suppressed"/>
/// so the scheduler <c>Cancel</c>s rather than retrying 6× (docs/36 §7).</item>
/// <item>ALL provider/transport exceptions are caught internally and collapsed to a FIXED generic error — the phone
/// number, credential and body NEVER reach a log or the delivery history (docs/36 §5).</item>
/// </list>
/// </summary>
public sealed class TwilioSmsProvider(IHttpClientFactory httpClientFactory) : ISmsProvider
{
    /// <summary>The only host this provider will ever attach credentials to (hardcoded allow-list, https-only).</summary>
    public const string ApiHost = "api.twilio.com";

    // A single fixed, PII-free error surfaced for every failure path (docs/36 §5) — never the raw provider body.
    private const string GenericError = "The SMS provider could not be reached.";

    // Twilio's carrier/recipient opt-out error code — a terminal suppression, not a retryable failure (docs/36 §7).
    private const int OptedOutErrorCode = 21610;

    public NotificationProviderKind Provider => NotificationProviderKind.Twilio;

    public async Task<SmsSendResult> SendAsync(SmsSendRequest request, CancellationToken cancellationToken)
    {
        HttpRequestMessage httpRequest;
        try
        {
            httpRequest = BuildRequest(request);
        }
        catch
        {
            // A malformed credential/number must never surface its value — collapse to the fixed generic error.
            return SmsSendResult.Fail(GenericError);
        }

        var client = httpClientFactory.CreateClient(ChatChannel.HttpClientName); // the shared SSRF-guarded outbound client
        try
        {
            using (httpRequest)
            using (var response = await client.SendAsync(httpRequest, cancellationToken))
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                return Classify(response.StatusCode, body);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // The SSRF guard throws for a disallowed address; transport AND timeout errors also land here. Collapse ALL
            // of them to the fixed generic error so nothing (number/credential/body) can leak (docs/36 §5).
            //
            // The second clause is what makes the timeout half of that true: this client has Timeout = 10s, and an
            // HttpClient timeout throws TaskCanceledException — which IS an OperationCanceledException — with our own
            // token uncancelled. Without it, the single most likely failure against a third-party REST API escapes the
            // one catch whose job is to keep credentials out of the delivery history.
            //
            // INDETERMINATE, not Fail (Wave 4 W4): the request left this process. Twilio may well have accepted it and
            // texted the recipient — we simply never saw the answer. Retrying is the one thing we must not do, because
            // it double-charges the operator and texts a real person twice, most likely mid-incident.
            return SmsSendResult.Unknown(GenericError);
        }
    }

    /// <summary>
    /// Verify an inbound Twilio webhook and parse it (docs/36 §5, Slice D). Twilio's scheme (its REAL algorithm — NOT
    /// Stripe's raw-body scheme): <c>Base64(HMAC-SHA1(authToken, URL + concat(key+value for every POST param sorted by
    /// key)))</c>, compared to the <c>X-Twilio-Signature</c> header. The URL is the caller's PINNED public webhook URL
    /// (never <c>Request.Host</c>). Fail-closed — ANY verification error returns null so the caller drops with 204.
    /// </summary>
    public InboundSmsMessage? VerifyAndParse(SmsInboundRequest request)
    {
        try
        {
            if (!VerifySignature(request))
            {
                return null;
            }

            // Verified — parse the normalized message. A verified inbound with no sender is unusable → fail closed.
            var from = request.Form.TryGetValue("From", out var f) ? f : null;
            if (string.IsNullOrWhiteSpace(from))
            {
                return null;
            }

            var to = request.Form.TryGetValue("To", out var t) ? t : string.Empty;
            var body = request.Form.TryGetValue("Body", out var b) ? b : string.Empty;
            return new InboundSmsMessage(from, to ?? string.Empty, body ?? string.Empty);
        }
        catch
        {
            // Fail closed on any unexpected error (e.g. a malformed credential) — never throw an oracle (docs/36 §5).
            return null;
        }
    }

    /// <summary>
    /// Twilio's webhook signature, verification half only (Wave 4 W4):
    /// <c>Base64(HMAC-SHA1(authToken, URL + concat(key+value for every POST param sorted by key)))</c> compared to the
    /// <c>X-Twilio-Signature</c> header with <see cref="CryptographicOperations.FixedTimeEquals"/>. The URL is the
    /// caller's PINNED public URL, never <c>Request.Host</c>. Fail-closed on any error.
    /// </summary>
    public bool VerifySignature(SmsInboundRequest request)
    {
        try
        {
            var (_, authToken) = SplitCredential(request.CredentialPlaintext);

            // Twilio canonical string: the exact URL, then each form param as key+value, params sorted by ordinal key.
            var canonical = new StringBuilder(request.Url);
            foreach (var pair in request.Form.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                canonical.Append(pair.Key).Append(pair.Value);
            }

            // HMAC-SHA1 is Twilio's PROTOCOL choice for this signature — we are VERIFYING their signature, not selecting a
            // hash for our own secrets, so the weak-hash analyzer does not apply here.
#pragma warning disable CA5350 // Do Not Use Weak Cryptographic Algorithms — Twilio's webhook signature is defined as HMAC-SHA1.
            var expected = HMACSHA1.HashData(Encoding.UTF8.GetBytes(authToken), Encoding.UTF8.GetBytes(canonical.ToString()));
#pragma warning restore CA5350

            byte[] provided;
            try
            {
                provided = Convert.FromBase64String(request.Signature);
            }
            catch (FormatException)
            {
                return false; // a malformed signature header is a verification failure, never an exception
            }

            return provided.Length == expected.Length && CryptographicOperations.FixedTimeEquals(expected, provided);
        }
        catch
        {
            return false; // fail closed on any unexpected error (e.g. a malformed credential) — never an oracle
        }
    }

    /// <summary>Build the Twilio Messages request from a hardcoded host — POST, Basic auth, form-encoded To/From/Body.</summary>
    private static HttpRequestMessage BuildRequest(SmsSendRequest request)
    {
        var (accountSid, authToken) = SplitCredential(request.CredentialPlaintext);
        var uri = new Uri($"https://{ApiHost}/2010-04-01/Accounts/{Uri.EscapeDataString(accountSid)}/Messages.json");

        // Belt-and-braces: the URL is assembled from a constant host, but never attach the operator's credential to
        // anything that isn't the allow-listed https host (defence-in-depth against a future refactor, docs/36 §5).
        if (uri.Scheme != Uri.UriSchemeHttps || !string.Equals(uri.Host, ApiHost, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("SMS provider host is not allow-listed.");
        }

        var form = new List<KeyValuePair<string, string>>
        {
            new("To", request.ToPhoneE164),
            new("From", request.FromValue),
            new("Body", request.Body),
        };

        // Ask Twilio to report the message's final state back to us (Wave 4 W4). This is what makes a send whose
        // response we lost recoverable: the callback names the SID and the outcome for a message we only know we
        // handed over. Omitted when the installation has pinned no public URL — there is nowhere honest to point.
        if (!string.IsNullOrWhiteSpace(request.StatusCallbackUrl))
        {
            form.Add(new KeyValuePair<string, string>("StatusCallback", request.StatusCallbackUrl));
        }

        var message = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new FormUrlEncodedContent(form),
        };
        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{accountSid}:{authToken}"));
        message.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
        return message;
    }

    // The credential is stored as "AccountSid:AuthToken" (also exactly Twilio's Basic-auth userinfo). Split on the FIRST
    // colon so an auth token containing a colon is preserved.
    private static (string AccountSid, string AuthToken) SplitCredential(string credential)
    {
        var idx = credential.IndexOf(':', StringComparison.Ordinal);
        if (idx <= 0 || idx >= credential.Length - 1)
        {
            throw new FormatException("SMS credential must be in 'AccountSid:AuthToken' form.");
        }

        return (credential[..idx], credential[(idx + 1)..]);
    }

    /// <summary>
    /// Map a Twilio HTTP response to an outcome. 21610 ⇒ suppressed (terminal); 2xx ⇒ ok (+ message SID); <b>5xx ⇒
    /// indeterminate</b>; any other non-2xx ⇒ a definite, retryable refusal carrying only the status code (never the raw
    /// body — docs/36 §5).
    /// <para>The 5xx split is the point (Wave 4 W4). A 4xx is Twilio telling us it rejected the request — nothing was
    /// queued, so retrying is safe and free. A 5xx is Twilio failing to tell us anything: it may have accepted the
    /// message before falling over. Treating the two the same is what turns one server-side blip into a duplicate text
    /// and a duplicate charge.</para>
    /// </summary>
    private static SmsSendResult Classify(HttpStatusCode status, string body)
    {
        var code = TryReadInt(body, "code");
        if (code == OptedOutErrorCode)
        {
            return SmsSendResult.OptedOut("Recipient has opted out of SMS (carrier STOP).");
        }

        if ((int)status is >= 200 and < 300)
        {
            return SmsSendResult.Ok(TryReadString(body, "sid"));
        }

        if ((int)status >= 500)
        {
            return SmsSendResult.Unknown($"SMS provider returned HTTP {(int)status}.");
        }

        return SmsSendResult.Fail($"SMS provider returned HTTP {(int)status}.");
    }

    private static int? TryReadInt(string json, string property)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                   && doc.RootElement.TryGetProperty(property, out var el)
                   && el.ValueKind == JsonValueKind.Number
                   && el.TryGetInt32(out var value)
                ? value
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? TryReadString(string json, string property)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                   && doc.RootElement.TryGetProperty(property, out var el)
                   && el.ValueKind == JsonValueKind.String
                ? el.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
