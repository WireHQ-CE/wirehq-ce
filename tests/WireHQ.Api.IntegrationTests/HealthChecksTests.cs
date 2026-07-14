using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WireHQ.Application.Abstractions.Persistence;
using WireHQ.Domain.Identity;
using Xunit;

namespace WireHQ.Api.IntegrationTests;

/// <summary>
/// Dependency health (docs/15 §13): liveness + readiness stay anonymous + simple, while the detailed
/// per-dependency snapshot at <c>/health/dependencies</c> is platform-operator only (Super Admin or Support)
/// and reports each dependency (database, SMTP, Stripe, agent gateway, Collector) — degrading gracefully for the
/// optional integrations rather than failing.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class HealthChecksTests(WireHqApiFactory factory)
{
    private readonly WireHqApiFactory _factory = factory;

    [Fact]
    public async Task Liveness_and_readiness_are_anonymous_and_the_dependency_detail_is_operator_only()
    {
        var client = _factory.CreateClient();

        // Liveness + readiness: anonymous and green (the test DB is up).
        (await client.GetAsync("/health/live")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/health/ready")).StatusCode.Should().Be(HttpStatusCode.OK);

        // The detailed endpoint challenges an anonymous caller.
        (await client.GetAsync("/health/dependencies")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // A normal authenticated user (no platform role) is forbidden.
        var userEmail = await RegisterAsync(client, "Health User");
        Authorize(client, await LoginAsync(client, userEmail));
        (await client.GetAsync("/health/dependencies")).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // A platform operator sees the per-dependency report.
        var superEmail = await RegisterAsync(client, "Health Super");
        await SetPlatformRoleAsync(superEmail, PlatformRole.SuperAdmin);
        Authorize(client, await LoginAsync(client, superEmail));

        var response = await client.GetAsync("/health/dependencies");
        response.StatusCode.Should().Be(HttpStatusCode.OK); // Degraded optionals still return 200 (only Unhealthy → 503)
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("status").GetString().Should().NotBeNullOrEmpty();

        var checks = doc.RootElement.GetProperty("checks").EnumerateArray().ToList();
        checks.Select(c => c.GetProperty("name").GetString())
            .Should().Contain(["database", "smtp", "stripe", "agent_gateway", "otlp_collector"]);
        checks.Single(c => c.GetProperty("name").GetString() == "database")
            .GetProperty("status").GetString().Should().Be("Healthy"); // the test DB is reachable
    }

    private async Task<string> RegisterAsync(HttpClient client, string name)
    {
        var email = $"{name.Replace(' ', '.').ToLower()}+{Guid.NewGuid():N}@wirehq.test";
        var response = await client.PostAsJsonAsync("/api/v1/auth/register",
            new { email, password = "Sup3rSecret!!", firstName = name, lastName = "Test", acceptTerms = true });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return email;
    }

    private async Task SetPlatformRoleAsync(string email, PlatformRole role)
    {
        using var scope = _factory.CreateBypassScope();
        var db = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
        var user = await db.Users.IgnoreQueryFilters().FirstAsync(u => u.Email.Value == email);
        user.SetPlatformRole(role);
        await db.SaveChangesAsync(CancellationToken.None);
    }

    private static async Task<string> LoginAsync(HttpClient client, string email)
    {
        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = "Sup3rSecret!!" });
        login.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await login.Content.ReadFromJsonAsync<TokenResponse>())!.AccessToken;
    }

    private static void Authorize(HttpClient client, string token) =>
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

    private sealed record TokenResponse(string AccessToken, int ExpiresIn);
}
