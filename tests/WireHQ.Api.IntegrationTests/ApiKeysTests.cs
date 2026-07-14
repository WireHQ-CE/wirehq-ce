using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WireHQ.Application.Abstractions;
using WireHQ.Application.Abstractions.Persistence;
using WireHQ.Application.Abstractions.Security;
using WireHQ.Domain.ApiKeys;
using WireHQ.Domain.Identity;
using WireHQ.Domain.Memberships;
using WireHQ.Domain.Organizations;
using Xunit;

namespace WireHQ.Api.IntegrationTests;

/// <summary>
/// API keys (docs/26-api-keys-webhooks.md, ADR-043). Exercises the real pipeline against Postgres: a key created
/// with a scope authenticates a request to a matching endpoint (via the API-key scheme), is denied on an endpoint
/// it wasn't scoped for, stops working when revoked, can't be minted with a scope the creator doesn't hold (the
/// escalation guard), and is feature-gated to Enterprise. Kept-core (usable in the CE, Enterprise by default).
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class ApiKeysTests : IDisposable
{
    private const string Password = "Sup3rSecret-Keys!1";

    private readonly WireHqApiFactory _factory;
    private readonly WebApplicationFactory<Program> _host;

    public ApiKeysTests(WireHqApiFactory factory)
    {
        _factory = factory;
        _host = factory.WithWebHostBuilder(_ => { });
    }

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task A_key_authenticates_a_request_within_its_scope_and_is_denied_outside_it()
    {
        var (client, _) = await AuthenticateOwnerAsync();

        // A key scoped to read users only.
        var key = await CreateKeyAsync(client, "CI reader", ["identity.users.read"]);

        var keyed = _host.CreateClient();
        keyed.DefaultRequestHeaders.Add("X-Api-Key", key);

        // In scope → 200.
        (await keyed.GetAsync("/api/v1/users")).StatusCode.Should().Be(HttpStatusCode.OK);

        // Out of scope (needs identity.roles.read, which the key lacks) → 403.
        (await keyed.GetAsync("/api/v1/roles")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_bearer_prefixed_key_also_authenticates()
    {
        var (client, _) = await AuthenticateOwnerAsync();
        var key = await CreateKeyAsync(client, "bearer key", ["identity.users.read"]);

        var keyed = _host.CreateClient();
        keyed.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);

        (await keyed.GetAsync("/api/v1/users")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_revoked_key_no_longer_authenticates()
    {
        var (client, _) = await AuthenticateOwnerAsync();
        var create = await client.PostAsJsonAsync("/api/v1/api-keys",
            new { name = "temp", scopes = new[] { "identity.users.read" }, expiresAtUtc = (DateTimeOffset?)null });
        var created = (await create.Content.ReadFromJsonAsync<CreateDto>())!;

        var keyed = _host.CreateClient();
        keyed.DefaultRequestHeaders.Add("X-Api-Key", created.Key);
        (await keyed.GetAsync("/api/v1/users")).StatusCode.Should().Be(HttpStatusCode.OK);

        (await client.DeleteAsync($"/api/v1/api-keys/{created.Id}")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await keyed.GetAsync("/api/v1/users")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task An_admin_cannot_grant_a_key_a_scope_they_do_not_hold()
    {
        var (_, organizationId) = await AuthenticateOwnerAsync();
        var admin = await AuthenticateAdminAsync(organizationId);

        // org.delete is Owner-only; an Admin holds api.keys.manage but not org.delete → escalation blocked.
        var response = await admin.PostAsJsonAsync("/api/v1/api-keys",
            new { name = "sneaky", scopes = new[] { "org.delete" }, expiresAtUtc = (DateTimeOffset?)null });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden); // api_key.scope_not_grantable
    }

    [Fact]
    public async Task Api_keys_are_feature_gated_to_enterprise()
    {
        var client = _host.CreateClient();
        var email = $"keyfree+{Guid.NewGuid():N}@wirehq.test";
        (await client.PostAsJsonAsync("/api/v1/auth/register",
            new { email, password = Password, firstName = "Key", lastName = "Owner", acceptTerms = true })).EnsureSuccessStatusCode();
        await _factory.VerifyEmailAsync(email);
        await Authenticate(client, email);

        var me = (await client.GetFromJsonAsync<MeDto>("/api/v1/auth/me"))!;
        await _factory.SetEditionAsync(me.ActiveOrganizationId!.Value, OrganizationEdition.Community);

        var response = await client.PostAsJsonAsync("/api/v1/api-keys",
            new { name = "x", scopes = new[] { "identity.users.read" }, expiresAtUtc = (DateTimeOffset?)null });
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden); // plan.upgrade_required
    }

    // Regression — the wave-1 security review. A key principal carries org + scopes + akid but deliberately NO
    // `sub`/`sid`, so ICurrentUser.UserId is null and a key can never act as its human creator on endpoints gated on
    // the *user identity* rather than a scope. Before the fix the key set sub=CreatedBy, so this key (scoped only to
    // read users) could revoke-all the creator's sessions (account DoS) and list their sessions (leak IPs/UAs).
    [Fact]
    public async Task A_key_cannot_act_on_its_creators_sessions()
    {
        var (owner, _) = await AuthenticateOwnerAsync();
        var key = await CreateKeyAsync(owner, "session hijack attempt", ["identity.users.read"]);

        var keyed = _host.CreateClient();
        keyed.DefaultRequestHeaders.Add("X-Api-Key", key);

        // Sanity: the key authenticates and works within its granted scope.
        (await keyed.GetAsync("/api/v1/users")).StatusCode.Should().Be(HttpStatusCode.OK);

        // Session management is gated on the *user* identity a key has not got → no user, no session ops.
        // "Log out everywhere" no-ops (401) instead of nuking the creator's sessions (was the account-DoS).
        (await keyed.PostAsync("/api/v1/sessions/revoke-all", content: null))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        // Listing sessions leaks nothing (was the IP/UA leak).
        (await keyed.GetAsync("/api/v1/sessions")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // And the owner's own session is untouched — no collateral damage.
        (await owner.GetAsync("/api/v1/auth/me")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // Regression — the wave-1 security review. Management (list/revoke) is entitlement-gated, so if authentication
    // ignored the plan an org that downgraded off Enterprise would be left with live keys it could no longer revoke.
    // The auth handler resolves the org's edition and fails closed, so a downgrade actually stops the keys.
    [Fact]
    public async Task A_key_stops_authenticating_after_the_org_downgrades_off_enterprise()
    {
        var (owner, organizationId) = await AuthenticateOwnerAsync();
        var key = await CreateKeyAsync(owner, "downgrade victim", ["identity.users.read"]);

        var keyed = _host.CreateClient();
        keyed.DefaultRequestHeaders.Add("X-Api-Key", key);
        (await keyed.GetAsync("/api/v1/users")).StatusCode.Should().Be(HttpStatusCode.OK);

        await _factory.SetEditionAsync(organizationId, OrganizationEdition.Community);

        // The org no longer holds api.keys → the key no longer authenticates (not merely un-manageable).
        (await keyed.GetAsync("/api/v1/users")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // The list projects an *effective* status — an expired-but-not-deleted key must read "Expired", not a misleading
    // "Active" (review polish). Also exercises the SQL translation of that projection (nothing else lists keys).
    [Fact]
    public async Task The_list_reports_effective_status_folding_in_expiry()
    {
        var (owner, organizationId) = await AuthenticateOwnerAsync();
        await CreateKeyAsync(owner, "live one", ["identity.users.read"]);

        // A key past its expiry — inserted directly (the create endpoint rejects a past expiry), same org so it lists.
        using (var scope = _factory.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<ISettableTenantContext>().SetBypass();
            var db = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
            var generated = ApiKeyToken.Generate();
            var expired = ApiKey.Create(
                organizationId, "expired one", generated.DisplayPrefix, generated.Hash,
                ["identity.users.read"], null, DateTimeOffset.UtcNow.AddDays(-1)).Value;
            db.ApiKeys.Add(expired);
            await db.SaveChangesAsync(default);
        }

        var list = (await owner.GetFromJsonAsync<List<KeyListItemDto>>("/api/v1/api-keys"))!;
        list.Should().Contain(k => k.Name == "live one" && k.Status == "Active");
        list.Should().Contain(k => k.Name == "expired one" && k.Status == "Expired");
    }

    private static async Task<string> CreateKeyAsync(HttpClient client, string name, string[] scopes)
    {
        var response = await client.PostAsJsonAsync("/api/v1/api-keys", new { name, scopes, expiresAtUtc = (DateTimeOffset?)null });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = (await response.Content.ReadFromJsonAsync<CreateDto>())!;
        created.Key.Should().StartWith("whq_");
        return created.Key;
    }

    private async Task<HttpClient> AuthenticateAdminAsync(Guid organizationId)
    {
        var email = $"keyadmin+{Guid.NewGuid():N}@wirehq.test";
        using (var scope = _factory.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<ISettableTenantContext>().SetBypass();
            var db = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
            var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();

            var user = User.Register(email, "Key Admin", hasher.Hash(Password)).Value;
            user.VerifyEmail();
            db.Users.Add(user);

            var adminRoleId = await db.Roles.IgnoreQueryFilters()
                .Where(r => r.OrganizationId == organizationId && r.Name == "Admin")
                .Select(r => r.Id).FirstAsync();
            db.Memberships.Add(Membership.CreateActive(organizationId, user.Id, [adminRoleId]));
            await db.SaveChangesAsync(default);
        }

        var client = _host.CreateClient();
        await Authenticate(client, email);
        return client;
    }

    private async Task<(HttpClient Client, Guid OrganizationId)> AuthenticateOwnerAsync()
    {
        var client = _host.CreateClient();
        var email = $"keyowner+{Guid.NewGuid():N}@wirehq.test";
        (await client.PostAsJsonAsync("/api/v1/auth/register",
            new { email, password = Password, firstName = "Key", lastName = "Owner", acceptTerms = true })).EnsureSuccessStatusCode();
        await _factory.VerifyEmailAsync(email);
        await Authenticate(client, email);

        var me = (await client.GetFromJsonAsync<MeDto>("/api/v1/auth/me"))!;
        await _factory.SetEditionAsync(me.ActiveOrganizationId!.Value, OrganizationEdition.Enterprise);
        return (client, me.ActiveOrganizationId!.Value);
    }

    private static async Task Authenticate(HttpClient client, string email)
    {
        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = Password });
        login.EnsureSuccessStatusCode();
        var token = (await login.Content.ReadFromJsonAsync<LoginDto>())!.AccessToken;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    private sealed record LoginDto(string AccessToken);
    private sealed record MeDto(Guid? ActiveOrganizationId);
    private sealed record CreateDto(Guid Id, string Key);
    private sealed record KeyListItemDto(Guid Id, string Name, string Status);
}
