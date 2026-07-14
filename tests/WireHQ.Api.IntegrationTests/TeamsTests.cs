using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WireHQ.Application.Abstractions;
using WireHQ.Application.Abstractions.Persistence;
using Xunit;

namespace WireHQ.Api.IntegrationTests;

/// <summary>
/// Teams CRUD + member assignment — the intra-tenant grouping that completes the
/// Super Admin → Customer → Team hierarchy. Proves the round-trip, tenant isolation, RBAC, and
/// that teams work for a customer admin and for a Super Admin while impersonating. (docs/03-multi-tenancy.md)
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class TeamsTests(WireHqApiFactory factory)
{
    private readonly WireHqApiFactory _factory = factory;

    [Fact]
    public async Task Admin_can_crud_a_team_and_manage_its_members()
    {
        var client = _factory.CreateClient();
        var (email, _) = await RegisterAsync(client, "Team Owner");
        Authorize(client, await LoginAsync(client, email));

        // Create
        var create = await client.PostAsJsonAsync("/api/v1/teams", new { name = "Platform Engineering", description = "Owns the core platform." });
        create.StatusCode.Should().Be(HttpStatusCode.Created);
        var teamId = (await create.Content.ReadFromJsonAsync<CreatedTeam>())!.Id;

        // List
        var list = (await client.GetFromJsonAsync<List<TeamListItemResponse>>("/api/v1/teams"))!;
        list.Should().ContainSingle(t => t.Id == teamId && t.Name == "Platform Engineering" && t.MemberCount == 0);

        // Add the org owner to the team BY EMAIL — they're already a member, so their membership is reused.
        var ownerMembershipId = (await client.GetFromJsonAsync<PagedResponse<UserItem>>("/api/v1/users"))!
            .Items.Single(u => u.Email == email).MembershipId;
        var addOwner = await client.PostAsJsonAsync($"/api/v1/teams/{teamId}/members", new { email });
        addOwner.StatusCode.Should().Be(HttpStatusCode.Created);
        (await addOwner.Content.ReadFromJsonAsync<AddMemberResult>())!.Outcome.Should().Be("AlreadyMember");

        // Invite a brand-NEW colleague by email + a chosen role → creates their org membership, emails an
        // accept-invite link, and puts them on the team. This is the headline behaviour.
        var adminRoleId = (await client.GetFromJsonAsync<List<RoleResponse>>("/api/v1/roles"))!
            .Single(r => r.Name == "Admin").Id;
        var colleagueEmail = $"colleague+{Guid.NewGuid():N}@wirehq.test";
        var invite = await client.PostAsJsonAsync($"/api/v1/teams/{teamId}/members",
            new { email = colleagueEmail, name = "New Colleague", roleId = adminRoleId });
        invite.StatusCode.Should().Be(HttpStatusCode.Created);
        (await invite.Content.ReadFromJsonAsync<AddMemberResult>())!.Outcome.Should().Be("InvitedNewUser");

        // The colleague is now a real org member (shows under Users) and is on the team.
        var usersAfter = (await client.GetFromJsonAsync<PagedResponse<UserItem>>("/api/v1/users"))!;
        usersAfter.Items.Should().Contain(u => u.Email == colleagueEmail);
        var colleagueMembershipId = usersAfter.Items.Single(u => u.Email == colleagueEmail).MembershipId;

        var detail = (await client.GetFromJsonAsync<TeamDetailResponse>($"/api/v1/teams/{teamId}"))!;
        detail.Members.Select(m => m.MembershipId).Should().BeEquivalentTo(new[] { ownerMembershipId, colleagueMembershipId });

        // The chosen role (Admin) was applied to the new membership, and the invite email was sent.
        await AssertMembershipHasRoleAsync(colleagueMembershipId, adminRoleId);
        var sender = (CapturingEmailSender)_factory.Services.GetRequiredService<IEmailSender>();
        sender.LastTo(colleagueEmail).Should().NotBeNull(because: "the invited colleague receives a set-password email");

        // Re-adding the colleague by email is idempotent (still two members).
        (await client.PostAsJsonAsync($"/api/v1/teams/{teamId}/members", new { email = colleagueEmail }))
            .StatusCode.Should().Be(HttpStatusCode.Created);
        (await client.GetFromJsonAsync<TeamDetailResponse>($"/api/v1/teams/{teamId}"))!.Members.Should().HaveCount(2);

        // Remove the colleague from the team (their org membership remains).
        (await client.DeleteAsync($"/api/v1/teams/{teamId}/members/{colleagueMembershipId}"))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await client.GetFromJsonAsync<TeamDetailResponse>($"/api/v1/teams/{teamId}"))!
            .Members.Should().ContainSingle(m => m.MembershipId == ownerMembershipId);

        // Rename.
        (await client.PatchAsJsonAsync($"/api/v1/teams/{teamId}", new { name = "Security" })).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await client.GetFromJsonAsync<TeamDetailResponse>($"/api/v1/teams/{teamId}"))!.Name.Should().Be("Security");

        // Delete.
        (await client.DeleteAsync($"/api/v1/teams/{teamId}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await client.GetAsync($"/api/v1/teams/{teamId}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Teams_are_isolated_to_their_organization()
    {
        var client = _factory.CreateClient();

        // Org A creates a team.
        var (emailA, _) = await RegisterAsync(client, "Org A Owner");
        Authorize(client, await LoginAsync(client, emailA));
        var teamId = (await (await client.PostAsJsonAsync("/api/v1/teams", new { name = $"A Team {Guid.NewGuid():N}" }))
            .Content.ReadFromJsonAsync<CreatedTeam>())!.Id;

        // Org B (a different tenant) cannot see or touch it.
        var (emailB, _) = await RegisterAsync(client, "Org B Owner");
        Authorize(client, await LoginAsync(client, emailB));

        (await client.GetFromJsonAsync<List<TeamListItemResponse>>("/api/v1/teams"))!
            .Should().NotContain(t => t.Id == teamId);
        (await client.GetAsync($"/api/v1/teams/{teamId}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.PatchAsJsonAsync($"/api/v1/teams/{teamId}", new { name = "Hijacked" })).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.DeleteAsync($"/api/v1/teams/{teamId}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // NB: the platform-tier slice (member RBAC + "an impersonating Super Admin can manage teams")
    // lives in TeamsImpersonationTests.cs — the SaaS-only file the Community Edition strip removes.

    // ---- helpers (mirror PlatformAdminTests) ----

    private async Task<(string Email, Guid UserId)> RegisterAsync(HttpClient client, string name)
    {
        var email = $"{name.Replace(' ', '.').ToLower()}+{Guid.NewGuid():N}@wirehq.test";
        var response = await client.PostAsJsonAsync("/api/v1/auth/register",
            new { email, password = "Sup3rSecret!!", firstName = name, lastName = "Test", acceptTerms = true });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = (await response.Content.ReadFromJsonAsync<RegisterResponse>())!;
        // Team management is behind the soft email-verify gate — confirm the email so these tests can run.
        await _factory.VerifyEmailAsync(email);
        return (email, body.UserId);
    }

    private static async Task<string> LoginAsync(HttpClient client, string email, string password = "Sup3rSecret!!")
    {
        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password });
        login.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await login.Content.ReadFromJsonAsync<TokenResponse>())!.AccessToken;
    }

    private static void Authorize(HttpClient client, string token) =>
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

    private async Task AssertMembershipHasRoleAsync(Guid membershipId, Guid roleId)
    {
        // Reads a tenant-owned table directly with no request context — opt out of RLS like trusted infra.
        using var scope = _factory.CreateBypassScope();
        var db = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
        var membership = await db.Memberships.IgnoreQueryFilters().FirstAsync(m => m.Id == membershipId);
        membership.Roles.Should().Contain(r => r.RoleId == roleId);
    }

    private sealed record RegisterResponse(Guid UserId, Guid OrganizationId, string OrganizationSlug);
    private sealed record TokenResponse(string AccessToken, int ExpiresIn);
    private sealed record CreatedTeam(Guid Id);
    private sealed record AddMemberResult(Guid TeamId, Guid MembershipId, string Outcome);
    private sealed record RoleResponse(Guid Id, string Name, string? Description, bool IsSystem);
    private sealed record TeamListItemResponse(Guid Id, string Name, string Slug, string? Description, int MemberCount, DateTimeOffset CreatedAtUtc);
    private sealed record TeamDetailResponse(Guid Id, string Name, string Slug, string? Description, DateTimeOffset CreatedAtUtc, List<TeamMemberResponse> Members);
    private sealed record TeamMemberResponse(Guid MembershipId, Guid UserId, string Name, string Email, string Status, DateTimeOffset AddedAtUtc);
    private sealed record PagedResponse<T>(List<T> Items, int Page, int PageSize, int Total, int TotalPages);
    private sealed record UserItem(Guid UserId, Guid MembershipId, string Email, string Name, string Status, DateTimeOffset? JoinedAtUtc);
}
