using FluentAssertions;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using WireHQ.Api.Observability;
using Xunit;

namespace WireHQ.Api.IntegrationTests;

/// <summary>
/// The redaction net (docs/15 §4): secrets are scrubbed from telemetry before it leaves the process — by
/// property name and by value shape — across every Serilog sink (console + OTLP). Plain unit tests (no host).
/// </summary>
public sealed class RedactionTests
{
    private readonly RedactionPolicy _policy = new();

    [Theory]
    [InlineData("Password")]
    [InlineData("DbPassword")]
    [InlineData("WebhookSecret")]
    [InlineData("ClientSecret")]
    [InlineData("AccessToken")]
    [InlineData("Authorization")]
    [InlineData("PrivateKey")]
    [InlineData("PreSharedKey")]
    [InlineData("ConnectionString")]
    public void Sensitive_property_names_are_flagged(string name) =>
        _policy.IsSensitiveProperty(name).Should().BeTrue();

    [Theory]
    [InlineData("CorrelationId")]
    [InlineData("RequestName")]
    [InlineData("ElapsedMs")]
    [InlineData("OrgId")]
    [InlineData("UserId")]
    public void Benign_property_names_are_not_flagged(string name) =>
        _policy.IsSensitiveProperty(name).Should().BeFalse();

    [Theory]
    // Deliberately short, obviously-fake values — they exercise the redaction regexes but stay under the
    // secret-scanner's length thresholds (these are not real keys).
    [InlineData("header eyJhbGciOiJI.eyJzdWIiOiIx.abc-123_DEF payload")] // JWT
    [InlineData("stripe sk_live_FAKEonly charged")] // Stripe key
    [InlineData("signing whsec_FAKEonly secret")] // Stripe webhook secret
    [InlineData("wg key gI6EdUSYvn8ugXOt8QQD6Yc+JyiZxIhp3GInSWRfWGE= applied")] // WireGuard key
    public void Secret_shaped_values_are_masked(string text)
    {
        var redacted = _policy.Redact(text);
        redacted.Should().Contain(RedactionPolicy.Mask);
        // The original secret substring must be gone.
        redacted.Should().NotContain("sk_live_FAKEonly")
            .And.NotContain("whsec_FAKEonly")
            .And.NotContain("eyJhbGciOiJI.eyJzdWIiOiIx.abc-123_DEF")
            .And.NotContain("gI6EdUSYvn8ugXOt8QQD6Yc+JyiZxIhp3GInSWRfWGE=");
    }

    [Theory]
    // The SMS inbound-webhook token (a show-once secret) reaches the request-summary log via {RequestPath} — the
    // trailing path segment is masked while the route stays visible (docs/36 §5).
    [InlineData("HTTP POST /api/v1/sms/inbound/tok_SECRETvalue123 responded 204", "/api/v1/sms/inbound/", "tok_SECRETvalue123")]
    [InlineData("url /api/v2/sms/inbound/AbC-dEf_123 next", "/api/v2/sms/inbound/", "AbC-dEf_123")]
    // The delivery-status callback carries the SAME token, so it must be masked identically — a leak there is
    // indistinguishable in consequence from a leak on the inbound route.
    [InlineData("HTTP POST /api/v1/sms/status/tok_SECRETvalue123 responded 204", "/api/v1/sms/status/", "tok_SECRETvalue123")]
    public void Sms_inbound_token_in_a_path_is_masked(string text, string keptPrefix, string secret)
    {
        var redacted = _policy.Redact(text);
        redacted.Should().Contain(keptPrefix + RedactionPolicy.Mask).And.NotContain(secret);
    }

    [Fact]
    public void The_log_and_span_planes_mask_the_token_identically()
    {
        // These are two entirely separate code paths — Serilog never sees a span attribute, and the span plane never
        // sees a log message. They shared no code until the token started reaching both, and the failure mode of them
        // drifting is silent: a route rename fixes one and leaves the other exporting the secret verbatim.
        const string path = "/api/v1/sms/inbound/tok_SECRETvalue123";

        _policy.Redact(path).Should().Be(SmsWebhookPathScrub.Apply(path))
            .And.NotContain("tok_SECRETvalue123");
    }

    [Fact]
    public void Connection_string_password_is_masked_keeping_the_key()
    {
        var redacted = _policy.Redact("Host=db;Username=app;Password=s3cr3t-pw;Database=wirehq");
        redacted.Should().Contain("Password=" + RedactionPolicy.Mask)
            .And.NotContain("s3cr3t-pw")
            .And.Contain("Host=db"); // non-secret parts untouched
    }

    [Fact]
    public void Clean_text_is_unchanged()
    {
        const string clean = "Handled CreateNetworkCommand in 16.4ms";
        _policy.Redact(clean).Should().Be(clean);
    }

    [Fact]
    public void Twilio_account_sid_is_scrubbed_from_a_span_url()
    {
        // The SID is a URL PATH segment (docs/36 §5, D-2) — the span-tag scrub masks it while keeping the endpoint shape.
        var scrubbed = HttpSpanScrubber.ScrubTwilioSid("https://api.twilio.com/2010-04-01/Accounts/ACdeadbeef1234/Messages.json");
        scrubbed.Should().Be("https://api.twilio.com/2010-04-01/Accounts/***/Messages.json").And.NotContain("ACdeadbeef1234");
    }

    [Fact]
    public void A_non_twilio_url_is_left_untouched_by_the_sid_scrub()
    {
        const string url = "https://hooks.slack.com/services/T0/B0/xxx";
        HttpSpanScrubber.ScrubTwilioSid(url).Should().Be(url);
    }

    [Fact]
    public void Enricher_redacts_sensitive_properties_and_secret_values_across_the_pipeline()
    {
        var sink = new CollectingSink();
        using var logger = new LoggerConfiguration()
            .Enrich.With(new RedactionEnricher(_policy))
            .WriteTo.Sink(sink)
            .CreateLogger();

        // A sensitive-named property is fully masked; a benign property carrying a JWT is value-scrubbed.
        logger.Information("Auth {Password} body {Body}", "hunter2", "token eyJhbGciOiJI.eyJzdWIiOiIx.sigDEF here");

        var ev = sink.Events.Should().ContainSingle().Subject;
        ev.Properties["Password"].ToString().Should().Contain("REDACTED");
        ev.Properties["Body"].ToString().Should().Contain("REDACTED").And.NotContain("eyJhbGciOiJI.eyJzdWIiOiIx.sigDEF");
        // The rendered message (the OTLP log body) is built from the now-redacted properties.
        ev.RenderMessage().Should().NotContain("hunter2").And.NotContain("eyJhbGciOiJI.eyJzdWIiOiIx.sigDEF");
    }

    private sealed class CollectingSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = [];

        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }
}
