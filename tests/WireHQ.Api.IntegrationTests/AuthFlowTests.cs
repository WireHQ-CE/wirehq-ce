using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Xunit;

namespace WireHQ.Api.IntegrationTests;

[Collection(ApiCollection.Name)]
public sealed class AuthFlowTests(WireHqApiFactory factory)
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Health_live_is_anonymous_and_ok()
    {
        var response = await _client.GetAsync("/health/live");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Register_then_login_issues_an_access_token()
    {
        var email = $"owner+{Guid.NewGuid():N}@wirehq.test";

        var register = await _client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            email,
            password = "Sup3rSecret!!",
            firstName = "Integration",
            lastName = "Owner",
            acceptTerms = true,
        });
        register.StatusCode.Should().Be(HttpStatusCode.Created);

        var login = await _client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = "Sup3rSecret!!" });
        login.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await login.Content.ReadFromJsonAsync<AuthTokenBody>();
        body.Should().NotBeNull();
        body!.AccessToken.Should().NotBeNullOrWhiteSpace();
        body.MfaRequired.Should().BeFalse();
    }

    [Fact]
    public async Task Protected_endpoint_requires_authentication()
    {
        var response = await _client.GetAsync("/api/v1/organizations/current");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private sealed record AuthTokenBody(string AccessToken, int ExpiresIn, bool MfaRequired);
}
