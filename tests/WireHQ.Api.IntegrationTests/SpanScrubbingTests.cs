using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using FluentAssertions;
using Xunit;

namespace WireHQ.Api.IntegrationTests;

/// <summary>
/// What the real server span carries after <c>ServerSpanScrubber</c> — end to end, through the actual ASP.NET Core
/// instrumentation. The scrubber runs in the instrumentation's <c>EnrichWithHttpRequest</c> callback, so it only works
/// if the instrumentation sets the URL tags BEFORE calling it and never rewrites them afterwards; an instrumentation
/// upgrade (or the framework emitting these tags itself) can break that ordering with every unit test still green, and
/// the result is SMS webhook tokens and searched phone numbers exported verbatim to the collector.
/// The listener reads each span at stop — the state the exporter sees — and the assertions cover EVERY tag, not just
/// the ones the scrubber knows about.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class SpanScrubbingTests : IDisposable
{
    private readonly WireHqApiFactory _factory;
    private readonly ConcurrentBag<Activity> _spans = [];
    private readonly ActivityListener _listener;

    public SpanScrubbingTests(WireHqApiFactory factory)
    {
        _factory = factory;
        _listener = new ActivityListener
        {
            // Server spans (Microsoft.AspNetCore) and outbound client spans (System.Net.Http).
            ShouldListenTo = source => source.Name is "Microsoft.AspNetCore" or "System.Net.Http",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = _spans.Add,
        };
        ActivitySource.AddActivityListener(_listener);
    }

    [Theory]
    [InlineData("inbound")]
    [InlineData("status")]
    public async Task An_sms_webhook_token_in_the_path_never_reaches_a_span(string kind)
    {
        var secret = $"tok{Guid.NewGuid():N}";

        var span = await SendTracedAsync(HttpMethod.Post, $"/api/v1/sms/{kind}/{secret}");
        TagValues(span).Should().NotContain(v => v.Contains(secret), "the token must be scrubbed from every tag");
        span.GetTagItem("url.path").Should().Be($"/api/v1/sms/{kind}/{WireHQ.Api.Observability.SmsWebhookPathScrub.Mask}");
    }

    /// <remarks>
    /// This one guards the end result rather than ServerSpanScrubber alone: OpenTelemetry's ASP.NET Core instrumentation
    /// already redacts query-string VALUES by default, so the number stays out even with the scrubber disconnected. It
    /// fails only if both layers stop working — which is still the property that matters.
    /// </remarks>
    [Fact]
    public async Task A_searched_phone_number_never_reaches_a_span()
    {
        var phone = $"+1555{Random.Shared.Next(1_000_000, 9_999_999)}";

        var span = await SendTracedAsync(HttpMethod.Get, $"/api/v1/notifications/sms/opt-outs?phone={Uri.EscapeDataString(phone)}");
        TagValues(span).Should().NotContain(v => v.Contains(phone.TrimStart('+')), "the searched number must be scrubbed");
    }

    [Fact]
    public async Task A_twilio_account_sid_in_an_outbound_url_never_reaches_a_span()
    {
        _ = _factory.Services; // the host (and its TracerProvider, which wires the enrichment) is running

        // A real request to http://api.twilio.com/… through the real handler — so the real HttpClient instrumentation
        // and its enrichment hook run — with the connection redirected to a local listener: no network involved.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using var connection = await listener.AcceptTcpClientAsync();
            var stream = connection.GetStream();
            _ = await stream.ReadAsync(new byte[4096]); // the request head; its content is irrelevant
            await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 204 No Content\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"));
        });
        using var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (_, ct) =>
            {
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                await socket.ConnectAsync(IPAddress.Loopback, port, ct);
                return new NetworkStream(socket, ownsSocket: true);
            },
        };
        using var client = new HttpClient(handler);
        var sid = $"AC{Guid.NewGuid():N}";
        var traceId = ActivityTraceId.CreateRandom();
        using var parent = new Activity("test-parent").SetParentId(traceId, ActivitySpanId.CreateRandom(), ActivityTraceFlags.Recorded).Start();

        (await client.PostAsync($"http://api.twilio.com/2010-04-01/Accounts/{sid}/Messages.json", new StringContent("")))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        parent.Stop();
        await server;

        var span = _spans.Should().ContainSingle(a => a.TraceId == traceId && a.Source.Name == "System.Net.Http").Subject;
        TagValues(span).Should().NotContain(v => v.Contains(sid), "the account SID must be scrubbed from every tag");
        span.GetTagItem("url.full").Should().Be("http://api.twilio.com/2010-04-01/Accounts/***/Messages.json");
    }

    /// <summary>Sends a request under a fresh trace id and returns exactly the server span it produced — matched by
    /// trace id, so spans from tests running concurrently elsewhere can never be picked up by mistake.</summary>
    private async Task<Activity> SendTracedAsync(HttpMethod method, string url)
    {
        var traceId = ActivityTraceId.CreateRandom();
        using var request = new HttpRequestMessage(method, url);
        if (method == HttpMethod.Post)
        {
            request.Content = new FormUrlEncodedContent([]);
        }

        request.Headers.Add("traceparent", $"00-{traceId}-{ActivitySpanId.CreateRandom()}-01");
        await _factory.CreateClient().SendAsync(request);

        return _spans.Should().ContainSingle(a => a.TraceId == traceId, "the request must have produced its server span").Subject;
    }

    private static List<string> TagValues(Activity activity) =>
        activity.TagObjects.Select(t => t.Value?.ToString() ?? string.Empty)
            .Append(activity.DisplayName)
            .ToList();

    public void Dispose() => _listener.Dispose();
}
