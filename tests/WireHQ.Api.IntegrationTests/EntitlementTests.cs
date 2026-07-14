using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using WireHQ.Domain.Organizations;
using Xunit;

namespace WireHQ.Api.IntegrationTests;

/// <summary>
/// Plan entitlements end-to-end: a Community org is blocked from Pro features (the EntitlementBehavior) and
/// hits its quotas (handler checks); the same org on Enterprise is unconstrained; and /me carries the plan +
/// features for UI gating. (docs/commercial.md §6 — entitlement core)
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class EntitlementTests(WireHqApiFactory factory)
{
    private readonly WireHqApiFactory _factory = factory;

    [Fact]
    public async Task A_Pro_feature_is_gated_by_plan()
    {
        var client = _factory.CreateClient();
        var orgId = await AuthenticateAsOwnerAsync(client);

        // Community: the fleet dashboard is a Pro+ feature → blocked.
        await _factory.SetEditionAsync(orgId, OrganizationEdition.Community);
        var blocked = await client.GetAsync("/api/v1/wireguard/fleet");
        blocked.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await blocked.Content.ReadAsStringAsync()).Should().Contain("plan.upgrade_required");

        // Enterprise: the same request is allowed.
        await _factory.SetEditionAsync(orgId, OrganizationEdition.Enterprise);
        (await client.GetAsync("/api/v1/wireguard/fleet")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task The_instance_quota_is_enforced_on_the_Community_plan()
    {
        var client = _factory.CreateClient();
        var orgId = await AuthenticateAsOwnerAsync(client);
        await _factory.SetEditionAsync(orgId, OrganizationEdition.Community);

        var networkId = await CreatedId(client.PostAsJsonAsync("/api/v1/wireguard/networks",
            new { name = "QuotaNet", cidr = "10.97.0.0/16" }));

        // Community allows 3 instances; the 4th is rejected with plan.limit_reached.
        for (var i = 1; i <= 3; i++)
        {
            (await client.PostAsJsonAsync("/api/v1/wireguard/instances",
                new { networkId, name = $"q{i}", listenPort = 51920 + i, interfaceAddress = $"10.97.{i}.1/24" }))
                .StatusCode.Should().Be(HttpStatusCode.Created, because: $"instance {i} is within the cap");
        }

        var overCap = await client.PostAsJsonAsync("/api/v1/wireguard/instances",
            new { networkId, name = "q4", listenPort = 51924, interfaceAddress = "10.97.4.1/24" });
        overCap.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await overCap.Content.ReadAsStringAsync()).Should().Contain("plan.limit_reached");

        // On Enterprise the cap is lifted.
        await _factory.SetEditionAsync(orgId, OrganizationEdition.Enterprise);
        (await client.PostAsJsonAsync("/api/v1/wireguard/instances",
            new { networkId, name = "q5", listenPort = 51925, interfaceAddress = "10.97.5.1/24" }))
            .StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Me_returns_the_plan_and_its_features()
    {
        var client = _factory.CreateClient();
        var orgId = await AuthenticateAsOwnerAsync(client);

        await _factory.SetEditionAsync(orgId, OrganizationEdition.Community);
        var community = (await client.GetFromJsonAsync<MeDto>("/api/v1/auth/me"))!;
        community.Entitlements.Plan.Should().Be("Community");
        community.Entitlements.Features.Should().NotContain("fleet.dashboard");

        await _factory.SetEditionAsync(orgId, OrganizationEdition.Enterprise);
        var enterprise = (await client.GetFromJsonAsync<MeDto>("/api/v1/auth/me"))!;
        enterprise.Entitlements.Plan.Should().Be("Enterprise");
        enterprise.Entitlements.Features.Should().Contain("fleet.dashboard");
    }

    private async Task<Guid> AuthenticateAsOwnerAsync(HttpClient client)
    {
        var email = $"ent+{Guid.NewGuid():N}@wirehq.test";
        const string password = "Sup3rSecret!!";
        (await client.PostAsJsonAsync("/api/v1/auth/register",
            new { email, password, firstName = "Ent", lastName = "Owner", acceptTerms = true })).EnsureSuccessStatusCode();
        await _factory.VerifyEmailAsync(email);
        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password });
        var token = (await login.Content.ReadFromJsonAsync<LoginDto>())!.AccessToken;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var me = (await client.GetFromJsonAsync<MeDto>("/api/v1/auth/me"))!;
        return me.ActiveOrganizationId!.Value;
    }

    private static async Task<Guid> CreatedId(Task<HttpResponseMessage> request)
    {
        var response = await request;
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<IdDto>())!.Id;
    }

    private sealed record LoginDto(string AccessToken);
    private sealed record IdDto(Guid Id);
    private sealed record MeDto(Guid? ActiveOrganizationId, EntitlementsDto Entitlements);
    private sealed record EntitlementsDto(string Plan, IReadOnlyCollection<string> Features, IReadOnlyDictionary<string, int> Limits);
}
