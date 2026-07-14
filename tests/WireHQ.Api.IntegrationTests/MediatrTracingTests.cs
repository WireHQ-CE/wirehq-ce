using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Xunit;

namespace WireHQ.Api.IntegrationTests;

/// <summary>
/// Phase 1 tracing (docs/15 §6): every use case opens a span on the <c>WireHQ.Application</c>
/// ActivitySource, nested under the ASP.NET request span — so it shares the trace id that is the
/// correlation reference (ADR-030) — and is tagged with the outcome. Proven through the real pipeline.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class MediatrTracingTests(WireHqApiFactory factory)
{
    private readonly WireHqApiFactory _factory = factory;

    [Fact]
    public async Task A_use_case_opens_a_span_on_the_application_source_tagged_with_the_outcome()
    {
        var spans = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "WireHQ.Application",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = spans.Add,
        };
        ActivitySource.AddActivityListener(listener);

        var client = _factory.CreateClient();
        await AuthenticateAsOwnerAsync(client);

        var create = await client.PostAsJsonAsync("/api/v1/wireguard/networks",
            new { name = "Trace Net", cidr = "10.50.0.0/24", dns = new[] { "1.1.1.1" } });
        create.StatusCode.Should().Be(HttpStatusCode.Created);

        var span = spans.Should().ContainSingle(s => s.DisplayName == "CreateNetworkCommand").Subject;
        span.GetTagItem("wirehq.outcome").Should().Be("ok");
        span.Status.Should().Be(ActivityStatusCode.Ok);
        // Nested under the request span → it carries the same trace id that is echoed as X-Correlation-Id.
        span.TraceId.ToString().Should().Be(create.Headers.GetValues("X-Correlation-Id").Single());
    }

    private async Task AuthenticateAsOwnerAsync(HttpClient client)
    {
        var email = $"owner+{Guid.NewGuid():N}@wirehq.test";
        const string password = "Sup3rSecret!!";

        var register = await client.PostAsJsonAsync("/api/v1/auth/register",
            new { email, password, firstName = "Trace", lastName = "Owner", acceptTerms = true });
        register.StatusCode.Should().Be(HttpStatusCode.Created);

        // Creating networks is gated by verified email (VerifiedEmailBehavior).
        await _factory.VerifyEmailAsync(email);

        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password });
        var token = (await login.Content.ReadFromJsonAsync<LoginResponse>())!.AccessToken;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    private sealed record LoginResponse(string AccessToken);
}
