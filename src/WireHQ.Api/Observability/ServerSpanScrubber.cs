using System.Diagnostics;
using Microsoft.AspNetCore.Http;

namespace WireHQ.Api.Observability;

/// <summary>
/// Scrubs secrets out of INBOUND request spans — the server-side counterpart to <see cref="HttpSpanScrubber"/>
/// (which covers outbound client spans).
///
/// Two things reach an OTel server span that must not leave the process:
/// <list type="bullet">
/// <item><b>The SMS inbound-webhook token.</b> It is a show-once bearer secret carried in the URL <b>path</b>
/// (docs/36 §5), so it lands in <c>url.path</c>/<c>http.target</c> on every webhook POST. OTel's built-in redaction
/// only covers query strings against a fixed vendor allow-list, and the Serilog <see cref="RedactionPolicy"/> is
/// logs-only — so without this the token was exported verbatim to the collector on every call.</item>
/// <item><b>Phone numbers in a query string.</b> The opt-out console searches by number, and an arbitrary
/// <c>?phone=</c> is not on any allow-list, so the whole query is dropped for that route rather than pattern-matched.
/// </item>
/// </list>
///
/// NB the metrics pipeline needs no equivalent: it dimensions on <c>http.route</c> — the route TEMPLATE, not the
/// resolved path — so a token never becomes a metric tag. That is asserted by a test, not assumed.
/// </summary>
public static class ServerSpanScrubber
{
    private const string OptOutsPath = "/api/v1/notifications/sms/opt-outs";

    /// <summary>The OTel <c>EnrichWithHttpRequest</c> callback.</summary>
    public static void Enrich(Activity activity, HttpRequest request)
    {
        var path = request.Path.HasValue ? request.Path.Value! : string.Empty;

        if (path.Contains("/sms/", StringComparison.OrdinalIgnoreCase))
        {
            var scrubbed = SmsWebhookPathScrub.Apply(path);
            if (!ReferenceEquals(scrubbed, path) && scrubbed != path)
            {
                // Rewrite every tag the path can reach. Stable across semconv versions: url.path is current,
                // http.target is the legacy name, and url.full carries the whole thing when it is populated.
                activity.SetTag("url.path", scrubbed);
                activity.SetTag("http.target", scrubbed);

                if (activity.GetTagItem("url.full") is string full)
                {
                    activity.SetTag("url.full", SmsWebhookPathScrub.Apply(full));
                }
            }
        }

        // Drop the query wholesale on the opt-out search rather than trying to match a phone parameter — a redaction
        // that has to enumerate parameter names is one rename away from leaking.
        if (path.StartsWith(OptOutsPath, StringComparison.OrdinalIgnoreCase) && request.QueryString.HasValue)
        {
            activity.SetTag("url.query", SmsWebhookPathScrub.Mask);
        }
    }
}
