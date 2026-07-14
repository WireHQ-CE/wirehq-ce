using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WireHQ.Application.Abstractions;
using WireHQ.Application.Abstractions.Persistence;
using WireHQ.Modules.Orchestration.Domain;
using Xunit;

namespace WireHQ.Api.IntegrationTests;

/// <summary>
/// Exercises the operator agent surface (Slice A, JWT pipeline, gated on <c>orch.agents.*</c>): minting a
/// single-use enrolment token returns the clear token exactly once and stores only its hash; agents list +
/// disable/revoke transition status; and the surface is tenant-isolated. The agent's own mTLS data plane
/// (<c>/agent/v1/*</c>) is covered separately. (ADR-028)
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class AgentOperatorApiTests(WireHqApiFactory factory)
{
    private readonly WireHqApiFactory _factory = factory;

    [Fact]
    public async Task Minting_a_token_returns_the_clear_token_once_and_stores_only_its_hash()
    {
        var client = _factory.CreateClient();
        await AuthenticateAsOwnerAsync(client);

        var response = await client.PostAsJsonAsync("/api/v1/agents/enroll-tokens", new { ttlHours = 2 });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var minted = (await response.Content.ReadFromJsonAsync<MintResponse>())!;
        minted.Token.Should().NotBeNullOrWhiteSpace();
        minted.ExpiresAtUtc.Should().BeAfter(DateTimeOffset.UtcNow);

        // The database holds only the hash of the clear token — never the token itself.
        using var scope = _factory.CreateBypassScope();
        var db = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
        var stored = await db.Set<AgentEnrollmentToken>().IgnoreQueryFilters().SingleAsync(t => t.Id == minted.Id);
        stored.TokenHash.Should().Be(AgentEnrollmentToken.HashToken(minted.Token));
        stored.TokenHash.Should().NotBe(minted.Token);
    }

    [Fact]
    public async Task Agents_list_then_disable_and_revoke_transition_status()
    {
        var client = _factory.CreateClient();
        var orgId = await AuthenticateAsOwnerAsync(client);
        var agentId = await SeedAgentAsync(orgId, "edge-01");

        var list = (await client.GetFromJsonAsync<List<AgentDto>>("/api/v1/agents"))!;
        list.Should().ContainSingle(a => a.Id == agentId).Which.Status.Should().Be("Active");

        (await client.PostAsync($"/api/v1/agents/{agentId}/disable", null)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await client.GetFromJsonAsync<AgentDto>($"/api/v1/agents/{agentId}"))!.Status.Should().Be("Disabled");

        (await client.PostAsync($"/api/v1/agents/{agentId}/revoke", null)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await client.GetFromJsonAsync<AgentDto>($"/api/v1/agents/{agentId}"))!.Status.Should().Be("Revoked");
    }

    [Fact]
    public async Task Agents_are_tenant_isolated()
    {
        var clientA = _factory.CreateClient();
        await AuthenticateAsOwnerAsync(clientA);

        var clientB = _factory.CreateClient();
        var orgB = await AuthenticateAsOwnerAsync(clientB);
        var bAgentId = await SeedAgentAsync(orgB, "b-only");

        // Org A sees none of org B's agents, and cannot fetch one by id.
        (await clientA.GetFromJsonAsync<List<AgentDto>>("/api/v1/agents"))!.Should().NotContain(a => a.Id == bAgentId);
        (await clientA.GetAsync($"/api/v1/agents/{bAgentId}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task The_agent_surface_requires_authentication()
    {
        var client = _factory.CreateClient();
        (await client.GetAsync("/api/v1/agents")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await client.PostAsJsonAsync("/api/v1/agents/enroll-tokens", new { })).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private async Task<Guid> SeedAgentAsync(Guid orgId, string name)
    {
        using var scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ISettableTenantContext>().SetTenant(orgId);
        var db = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();

        var agent = Agent.Enroll(
            Guid.CreateVersion7(), orgId, name,
            certificateFingerprint: Guid.NewGuid().ToString("N").ToUpperInvariant(),
            certificatePem: "-----BEGIN CERTIFICATE-----\nSEEDED\n-----END CERTIFICATE-----",
            platform: "linux-amd64", enrolledAtUtc: DateTimeOffset.UtcNow).Value;
        db.Set<Agent>().Add(agent);
        await db.SaveChangesAsync(CancellationToken.None);
        return agent.Id;
    }

    private static async Task<Guid> AuthenticateAsOwnerAsync(HttpClient client)
    {
        var email = $"owner+{Guid.NewGuid():N}@wirehq.test";
        const string password = "Sup3rSecret!!";

        var register = await client.PostAsJsonAsync("/api/v1/auth/register",
            new { email, password, firstName = "Agent Owner", lastName = "Test", acceptTerms = true });
        register.StatusCode.Should().Be(HttpStatusCode.Created);
        var orgId = (await register.Content.ReadFromJsonAsync<RegisterResponse>())!.OrganizationId;

        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password });
        var token = (await login.Content.ReadFromJsonAsync<LoginResponse>())!.AccessToken;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return orgId;
    }

    private sealed record MintResponse(Guid Id, string Token, DateTimeOffset ExpiresAtUtc);
    private sealed record AgentDto(Guid Id, string Name, string Status, string? Platform, string? Version);
    private sealed record RegisterResponse(Guid UserId, Guid OrganizationId, string OrganizationSlug);
    private sealed record LoginResponse(string AccessToken);
}
