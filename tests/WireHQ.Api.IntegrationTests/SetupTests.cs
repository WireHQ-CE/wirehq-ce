using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Xunit;

namespace WireHQ.Api.IntegrationTests;

/// <summary>
/// The browser first-run setup (docs/17-community-edition.md): a fresh, ownerless self-hosted
/// instance (<c>Setup:Enabled=true</c>) sends its first visitor to the in-browser wizard, which
/// claims the instance via <c>POST /auth/setup</c>. These tests pin the GUARDS — disabled by
/// default (the SaaS posture) and hard-refused once any user exists, so an established instance
/// can never be re-claimed. The happy path needs an EMPTY users table, which the shared-DB fixture
/// can't provide — it is verified end-to-end on the generated Community Edition artifact
/// (fresh Postgres → /setup wizard → owner lands on the dashboard) per the CE verify workflow.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class SetupTests(WireHqApiFactory factory)
{
    private readonly WireHqApiFactory _factory = factory;

    [Fact]
    public async Task Setup_is_disabled_by_default()
    {
        var client = _factory.CreateClient();

        // The SaaS posture: no Setup:Enabled ⇒ the endpoint refuses and the config never routes to it.
        var config = await client.GetFromJsonAsync<SecurityConfigDto>("/api/v1/auth/security-config");
        config!.SetupRequired.Should().BeFalse(because: "setup is opt-in — the SaaS never shows it");

        var attempt = await client.PostAsJsonAsync("/api/v1/auth/setup", ValidPayload());
        attempt.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await attempt.Content.ReadAsStringAsync()).Should().Contain("setup.not_available");
    }

    [Fact]
    public async Task Setup_refuses_once_any_user_exists_even_when_enabled()
    {
        // Guarantee the instance is "claimed" (the shared DB has users from other suites anyway).
        var register = await _factory.CreateClient().PostAsJsonAsync("/api/v1/auth/register", new
        {
            email = $"claimant+{Guid.NewGuid():N}@wirehq.test",
            password = "Sup3rSecret-Claim!1",
            firstName = "Already",
            lastName = "Claimed",
            acceptTerms = true,
        });
        register.EnsureSuccessStatusCode();

        // A derived host with the self-host posture — but the instance has users, so no setup.
        using var selfHost = _factory.WithWebHostBuilder(builder =>
            builder.UseSetting("Setup:Enabled", "true"));
        var client = selfHost.CreateClient();

        var config = await client.GetFromJsonAsync<SecurityConfigDto>("/api/v1/auth/security-config");
        config!.SetupRequired.Should().BeFalse(because: "a claimed instance never re-enters setup");

        var attempt = await client.PostAsJsonAsync("/api/v1/auth/setup", ValidPayload());
        attempt.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await attempt.Content.ReadAsStringAsync()).Should().Contain("setup.not_available");
    }

    [Fact]
    public async Task Setup_validates_its_payload()
    {
        using var selfHost = _factory.WithWebHostBuilder(builder =>
            builder.UseSetting("Setup:Enabled", "true"));
        var client = selfHost.CreateClient();

        // Validation runs before the availability guard, mirroring registration's rules.
        var attempt = await client.PostAsJsonAsync("/api/v1/auth/setup", new
        {
            email = "not-an-email",
            firstName = "",
            lastName = "Owner",
            password = "short",
        });

        attempt.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await attempt.Content.ReadAsStringAsync();
        body.Should().Contain("email").And.Contain("firstName").And.Contain("password");
    }

    private static object ValidPayload() => new
    {
        email = $"setup-owner+{Guid.NewGuid():N}@wirehq.test",
        firstName = "Setup",
        lastName = "Owner",
        password = "Sup3rSecret-Setup!1",
        organizationName = "Setup Org",
    };

    private sealed record SecurityConfigDto(
        bool TurnstileEnabled, string? TurnstileSiteKey, bool RegistrationEnabled, bool SetupRequired);
}
