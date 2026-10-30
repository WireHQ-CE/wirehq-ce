using System.Diagnostics;
using System.Text.RegularExpressions;
using WireHQ.Infrastructure.Messaging.Notifications.Sms;

namespace WireHQ.Api.Observability;

/// <summary>
/// Scrubs provider secrets that live in an outbound HTTP request's URL <b>path</b> out of its OTel client span
/// (docs/36 §5, Wave 4 Slice D precondition D-2). The Twilio Account SID is a path segment
/// (<c>Accounts/{sid}/Messages.json</c>), so it leaks into the <c>url.full</c>/<c>http.url</c> span attribute once the
/// SMS send path emits spans. OTel's built-in query-string redaction can't touch a path segment, and the Serilog
/// <see cref="RedactionPolicy"/> is <b>logs-only</b> (it never sees a span attribute) — so the tag is rewritten here, in
/// the <c>HttpClient</c> instrumentation enrichment, filtered by request <b>host</b> (the instrumentation is
/// process-global; it can't be scoped to a named client). Dark today — no SMS spans are emitted until the send-gate flips.
/// </summary>
public static partial class HttpSpanScrubber
{
    /// <summary>The OTel <c>EnrichWithHttpRequestMessage</c> callback: for a Twilio request, replace the SID in the span's
    /// URL tags with <c>***</c>.</summary>
    public static void EnrichHttpSpan(Activity activity, HttpRequestMessage request)
    {
        if (request.RequestUri is not { } uri
            || !string.Equals(uri.Host, TwilioSmsProvider.ApiHost, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var scrubbed = ScrubTwilioSid(uri.AbsoluteUri);
        activity.SetTag("url.full", scrubbed);   // current OTel semantic-convention attribute
        activity.SetTag("http.url", scrubbed);   // legacy attribute still emitted by some exporters
    }

    /// <summary>Replace the Twilio Account SID path segment (<c>/Accounts/{sid}</c>) with <c>/Accounts/***</c>. Pure +
    /// unit-tested; leaves a non-Twilio URL untouched.</summary>
    public static string ScrubTwilioSid(string url) => TwilioAccountSidPattern().Replace(url, "/Accounts/***");

    [GeneratedRegex(@"/Accounts/[^/?#]+")]
    private static partial Regex TwilioAccountSidPattern();
}
