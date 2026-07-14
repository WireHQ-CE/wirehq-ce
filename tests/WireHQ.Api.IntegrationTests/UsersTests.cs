using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Xunit;

namespace WireHQ.Api.IntegrationTests;

/// <summary>
/// Regression guard for the org-scoped user list. <c>GET /api/v1/users</c> joins the global users
/// table with <c>IgnoreQueryFilters()</c>, which is query-wide — without an explicit org predicate it
/// would also disable the Memberships tenant filter and leak every org's members. (docs/03-multi-tenancy.md)
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class UsersTests(WireHqApiFactory factory)
{
    private readonly WireHqApiFactory _factory = factory;

    [Fact]
    public async Task User_list_only_returns_the_active_organizations_members()
    {
        var client = _factory.CreateClient();

        // Two independent tenants, each with one owner.
        var (emailA, _) = await RegisterAsync(client, "Tenant A Owner");
        var (emailB, _) = await RegisterAsync(client, "Tenant B Owner");

        // As tenant A, the user list contains A's owner and NOT tenant B's owner.
        Authorize(client, await LoginAsync(client, emailA));
        var users = (await client.GetFromJsonAsync<PagedResponse<UserItem>>("/api/v1/users"))!;

        users.Items.Should().Contain(u => u.Email == emailA);
        users.Items.Should().NotContain(u => u.Email == emailB,
            because: "the user list must be scoped to the active organization");
    }

    private async Task<(string Email, Guid UserId)> RegisterAsync(HttpClient client, string name)
    {
        var email = $"{name.Replace(' ', '.').ToLower()}+{Guid.NewGuid():N}@wirehq.test";
        var response = await client.PostAsJsonAsync("/api/v1/auth/register",
            new { email, password = "Sup3rSecret!!", firstName = name, lastName = "Test", acceptTerms = true });
        var body = (await response.Content.ReadFromJsonAsync<RegisterResponse>())!;
        return (email, body.UserId);
    }

    private static async Task<string> LoginAsync(HttpClient client, string email)
    {
        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = "Sup3rSecret!!" });
        return (await login.Content.ReadFromJsonAsync<TokenResponse>())!.AccessToken;
    }

    private static void Authorize(HttpClient client, string token) =>
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

    private sealed record RegisterResponse(Guid UserId, Guid OrganizationId, string OrganizationSlug);
    private sealed record TokenResponse(string AccessToken, int ExpiresIn);
    private sealed record PagedResponse<T>(List<T> Items, int Page, int PageSize, int Total, int TotalPages);
    private sealed record UserItem(Guid UserId, Guid MembershipId, string Email, string Name, string Status, DateTimeOffset? JoinedAtUtc);
}
