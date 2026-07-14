using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace WireHQ.Api.IntegrationTests;

/// <summary>
/// The correlation spine (ADR-030): every response carries an <c>X-Correlation-Id</c> header that is the
/// W3C trace id, and error bodies — including from module minimal-API endpoints, which previously emitted
/// no trace id — carry the same id as <c>traceId</c>, so one quotable reference ties UI → API → logs.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class CorrelationTests(WireHqApiFactory factory)
{
    private readonly WireHqApiFactory _factory = factory;

    [Fact]
    public async Task Every_response_carries_the_w3c_trace_id_as_a_correlation_header()
    {
        var client = _factory.CreateClient();

        // Any anonymous endpoint works — security-config exists in every edition (the SaaS-only
        // /config/plans previously used here is stripped from the Community Edition).
        var response = await client.GetAsync("/api/v1/auth/security-config");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.TryGetValues("X-Correlation-Id", out var values).Should().BeTrue();
        var correlationId = values!.Single();
        // The unified id is the W3C trace id (32 lowercase hex), not the old connection TraceIdentifier.
        correlationId.Should().MatchRegex("^[0-9a-f]{32}$");
    }

    [Fact]
    public async Task Module_endpoint_errors_carry_a_traceId_matching_the_correlation_header()
    {
        var client = _factory.CreateClient();
        await AuthenticateAsOwnerAsync(client);

        // A module (minimal-API) endpoint that returns a NotFound Result through the shared mapper —
        // previously these carried no trace id at all.
        var response = await client.GetAsync($"/api/v1/wireguard/instances/{Guid.NewGuid()}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var header = response.Headers.GetValues("X-Correlation-Id").Single();

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.TryGetProperty("traceId", out var traceId).Should().BeTrue();
        traceId.GetString().Should().NotBeNullOrEmpty().And.Be(header);
    }

    private static async Task AuthenticateAsOwnerAsync(HttpClient client)
    {
        var email = $"owner+{Guid.NewGuid():N}@wirehq.test";
        const string password = "Sup3rSecret!!";

        var register = await client.PostAsJsonAsync("/api/v1/auth/register",
            new { email, password, firstName = "Corr", lastName = "Test", acceptTerms = true });
        register.StatusCode.Should().Be(HttpStatusCode.Created);

        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password });
        var token = (await login.Content.ReadFromJsonAsync<LoginResponse>())!.AccessToken;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    private sealed record LoginResponse(string AccessToken);
}
