using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Xunit;

namespace WireHQ.Api.IntegrationTests;

/// <summary>Per-user notification preferences: sensible defaults, then a get/update round-trip.</summary>
[Collection(ApiCollection.Name)]
public sealed class NotificationPreferencesTests(WireHqApiFactory factory)
{
    private readonly WireHqApiFactory _factory = factory;

    [Fact]
    public async Task Defaults_then_update_round_trips()
    {
        var client = _factory.CreateClient();
        var email = $"notify+{Guid.NewGuid():N}@wirehq.test";
        var register = await client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            email, password = "Sup3rSecret!!", firstName = "Nora", lastName = "Tify", acceptTerms = true,
        });
        register.StatusCode.Should().Be(HttpStatusCode.Created);
        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = "Sup3rSecret!!" });
        var token = (await login.Content.ReadFromJsonAsync<TokenBody>())!.AccessToken;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // Defaults: everything on except marketing.
        var defaults = (await client.GetFromJsonAsync<Prefs>("/api/v1/account/notifications"))!;
        defaults.SecurityAlerts.Should().BeTrue();
        defaults.VpnStatusAlerts.Should().BeTrue();
        defaults.ProductAnnouncements.Should().BeTrue();
        defaults.BillingNotifications.Should().BeTrue();
        defaults.MarketingEmails.Should().BeFalse();

        // Update + persist.
        (await client.PutAsJsonAsync("/api/v1/account/notifications", new
        {
            securityAlerts = true,
            vpnStatusAlerts = false,
            productAnnouncements = false,
            billingNotifications = true,
            marketingEmails = true,
        })).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var saved = (await client.GetFromJsonAsync<Prefs>("/api/v1/account/notifications"))!;
        saved.VpnStatusAlerts.Should().BeFalse();
        saved.ProductAnnouncements.Should().BeFalse();
        saved.MarketingEmails.Should().BeTrue();
    }

    private sealed record TokenBody(string AccessToken, int ExpiresIn);
    private sealed record Prefs(bool SecurityAlerts, bool VpnStatusAlerts, bool ProductAnnouncements, bool BillingNotifications, bool MarketingEmails);
}
