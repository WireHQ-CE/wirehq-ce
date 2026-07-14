using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Xunit;

namespace WireHQ.Api.IntegrationTests;

/// <summary>
/// The core organization-profile round-trip (Settings → Organization): the founding Owner updates
/// the expanded business profile and reads it back. Platform-free by design — the SaaS-tier slice
/// (billing profile + platform-provisioned member RBAC) lives in <see cref="OrganizationSettingsTests"/>,
/// which the Community Edition strip removes (docs/17 §5); this file is the coverage the CE keeps.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class OrganizationProfileTests(WireHqApiFactory factory)
{
    private readonly WireHqApiFactory _factory = factory;

    [Fact]
    public async Task Owner_updates_the_org_profile_and_reads_it_back()
    {
        var client = _factory.CreateClient();
        var email = $"org.profile+{Guid.NewGuid():N}@wirehq.test";
        (await client.PostAsJsonAsync("/api/v1/auth/register",
            new { email, password = "Sup3rSecret!!", firstName = "Profile", lastName = "Owner", acceptTerms = true }))
            .StatusCode.Should().Be(HttpStatusCode.Created);
        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = "Sup3rSecret!!" });
        var token = (await login.Content.ReadFromJsonAsync<TokenResponse>())!.AccessToken;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var patch = await client.PatchAsJsonAsync("/api/v1/organizations/current", new
        {
            name = "Acme Renamed",
            legalName = "Acme Industries Ltd",
            website = "https://acme.example",
            industry = "Software",
            companySize = "51–200",
            country = "GB",
            timezone = "Europe/London",
        });
        patch.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var org = (await client.GetFromJsonAsync<OrgResponse>("/api/v1/organizations/current"))!;
        org.Name.Should().Be("Acme Renamed");
        org.LegalName.Should().Be("Acme Industries Ltd");
        org.Website.Should().Be("https://acme.example");
        org.Industry.Should().Be("Software");
        org.CompanySize.Should().Be("51–200");
        org.Country.Should().Be("GB");
        org.Timezone.Should().Be("Europe/London");
    }

    private sealed record TokenResponse(string AccessToken, int ExpiresIn);
    private sealed record OrgResponse(
        Guid Id, string Slug, string Name, string Status, string Edition,
        string? LegalName, string? Website, string? Industry, string? CompanySize, string? Country, string? Timezone,
        int MemberCount, int TeamCount, DateTimeOffset CreatedAtUtc);
}
