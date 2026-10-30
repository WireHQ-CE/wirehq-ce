using System.Text.RegularExpressions;
using WireHQ.Shared.Observability;

namespace WireHQ.Api.Observability;

/// <summary>
/// The one place that knows how to mask the SMS inbound-webhook token out of a URL path.
///
/// The token is a show-once bearer secret carried in the path (docs/36 §5), so it reaches BOTH observability planes
/// and they are entirely separate code paths: the Serilog <see cref="RedactionPolicy"/> covers log messages, and it
/// never sees a span attribute (see <see cref="HttpSpanScrubber"/>). Masking used to live only in the log plane, which
/// meant the token exported verbatim in the OTel server span's <c>url.path</c> on every webhook POST.
///
/// Both planes now share this pattern, so a route change cannot fix one and silently leave the other leaking.
/// </summary>
public static partial class SmsWebhookPathScrub
{
    /// <summary>The one mask used everywhere — a second literal here would render differently in the two planes.</summary>
    public const string Mask = SensitiveData.Mask;

    /// <summary>
    /// The token is the segment after the route. <c>status</c> is covered alongside <c>inbound</c> because the
    /// delivery-status callback carries the same token — a leak there is identical in consequence.
    /// </summary>
    [GeneratedRegex(@"(/sms/(?:inbound|status))/[^/\s?#]+", RegexOptions.IgnoreCase)]
    private static partial Regex TokenInPathPattern();

    /// <summary>Replaces the token segment, keeping the route visible: <c>/sms/inbound/***</c>.</summary>
    public static string Apply(string text) => TokenInPathPattern().Replace(text, $"$1/{Mask}");
}
