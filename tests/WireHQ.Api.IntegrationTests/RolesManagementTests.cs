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
using WireHQ.Domain.Identity;
using WireHQ.Domain.Memberships;
using WireHQ.Domain.Organizations;
using Xunit;

namespace WireHQ.Api.IntegrationTests;

/// <summary>
/// Custom roles — the gated write path on the kept-core <c>Role</c> aggregate (docs/25-custom-roles.md, ADR-042).
/// Exercises create / get-detail / update (rename + REPLACE the permission set — the owned-collection path) /
/// delete against real Postgres, plus the guards: a system role is immutable, a role in use can't be deleted, the
/// feature-gate blocks a non-Enterprise org, and the privilege-escalation guard stops an Admin granting an
/// Owner-only permission. <c>rbac.custom_roles</c> is granted to Enterprise.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class RolesManagementTests : IDisposable
{
    private const string Password = "Sup3rSecret-Roles!1";

    private readonly WireHqApiFactory _factory;
    private readonly WebApplicationFactory<Program> _host;

    public RolesManagementTests(WireHqApiFactory factory)
    {
        _factory = factory;
        _host = factory.WithWebHostBuilder(_ => { });
    }

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task Owner_creates_edits_and_deletes_a_custom_role()
    {
        var (client, _) = await AuthenticateOwnerAsync();
        var perms = await PermissionMapAsync(client);

        // Create with an initial permission set.
        var create = await client.PostAsJsonAsync("/api/v1/roles", new
        {
            name = "Network Operator",
            description = "Manage networks and view audit",
            permissionIds = new[] { perms["identity.users.read"], perms["identity.teams.read"] },
        });
        create.StatusCode.Should().Be(HttpStatusCode.Created);
        var roleId = (await create.Content.ReadFromJsonAsync<CreateDto>())!.Id;

        var detail = await GetRoleAsync(client, roleId);
        detail.IsSystem.Should().BeFalse();
        detail.PermissionIds.Should().BeEquivalentTo(new[] { perms["identity.users.read"], perms["identity.teams.read"] });

        // Update: rename + REPLACE the permission set (remove one, add another) — the owned-collection diff path.
        var update = await client.PutAsJsonAsync($"/api/v1/roles/{roleId}", new
        {
            name = "Network Admin",
            description = "Renamed",
            permissionIds = new[] { perms["identity.teams.read"], perms["audit.logs.read"] },
        });
        update.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var afterUpdate = await GetRoleAsync(client, roleId);
        afterUpdate.Name.Should().Be("Network Admin");
        afterUpdate.PermissionIds.Should().BeEquivalentTo(new[] { perms["identity.teams.read"], perms["audit.logs.read"] });

        // Delete (not assigned to anyone) → gone.
        (await client.DeleteAsync($"/api/v1/roles/{roleId}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await client.GetAsync($"/api/v1/roles/{roleId}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_system_role_cannot_be_edited_or_deleted()
    {
        var (client, _) = await AuthenticateOwnerAsync();
        var roles = (await client.GetFromJsonAsync<IReadOnlyList<RoleListDto>>("/api/v1/roles"))!;
        var adminRole = roles.First(r => r.Name == "Admin" && r.IsSystem);

        var edit = await client.PutAsJsonAsync($"/api/v1/roles/{adminRole.Id}", new { name = "Admin2", description = (string?)null, permissionIds = Array.Empty<Guid>() });
        edit.StatusCode.Should().Be(HttpStatusCode.Conflict); // role.system_immutable

        (await client.DeleteAsync($"/api/v1/roles/{adminRole.Id}")).StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task A_role_that_is_in_use_cannot_be_deleted()
    {
        var (client, organizationId) = await AuthenticateOwnerAsync();
        var perms = await PermissionMapAsync(client);

        var create = await client.PostAsJsonAsync("/api/v1/roles", new
        {
            name = "Assigned Role",
            description = (string?)null,
            permissionIds = new[] { perms["identity.users.read"] },
        });
        var roleId = (await create.Content.ReadFromJsonAsync<CreateDto>())!.Id;

        // Assign the role to a member (directly, via a bypass scope) so the delete guard trips.
        await AssignRoleToAMemberAsync(organizationId, roleId);

        (await client.DeleteAsync($"/api/v1/roles/{roleId}")).StatusCode.Should().Be(HttpStatusCode.Conflict); // role.in_use
    }

    [Fact]
    public async Task An_admin_cannot_grant_a_permission_they_do_not_hold()
    {
        var (owner, organizationId) = await AuthenticateOwnerAsync();
        var perms = await PermissionMapAsync(owner);
        var adminClient = await AuthenticateAdminAsync(organizationId);

        // org.delete is Owner-only; an Admin holds identity.roles.manage but not org.delete → escalation blocked.
        var response = await adminClient.PostAsJsonAsync("/api/v1/roles", new
        {
            name = "Sneaky",
            description = (string?)null,
            permissionIds = new[] { perms["org.delete"] },
        });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden); // role.permission_not_grantable
    }

    [Fact]
    public async Task Custom_roles_are_feature_gated_to_enterprise()
    {
        var client = _host.CreateClient();
        var email = $"role+{Guid.NewGuid():N}@wirehq.test";
        (await client.PostAsJsonAsync("/api/v1/auth/register",
            new { email, password = Password, firstName = "Role", lastName = "Owner", acceptTerms = true })).EnsureSuccessStatusCode();
        await _factory.VerifyEmailAsync(email);
        await Authenticate(client, email);

        // The suite defaults new orgs to Enterprise; downgrade to Community so the entitlement gate applies.
        var me = (await client.GetFromJsonAsync<MeDto>("/api/v1/auth/me"))!;
        await _factory.SetEditionAsync(me.ActiveOrganizationId!.Value, OrganizationEdition.Community);

        var response = await client.PostAsJsonAsync("/api/v1/roles", new { name = "X", description = (string?)null, permissionIds = Array.Empty<Guid>() });
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden); // plan.upgrade_required
    }

    private static async Task<RoleDetailDto> GetRoleAsync(HttpClient client, Guid id) =>
        (await client.GetFromJsonAsync<RoleDetailDto>($"/api/v1/roles/{id}"))!;

    private static async Task<Dictionary<string, Guid>> PermissionMapAsync(HttpClient client)
    {
        var perms = (await client.GetFromJsonAsync<IReadOnlyList<PermissionDto>>("/api/v1/roles/permissions"))!;
        return perms.ToDictionary(p => p.Key, p => p.Id);
    }

    private async Task AssignRoleToAMemberAsync(Guid organizationId, Guid roleId)
    {
        // Create a fresh member holding the role (assigning at construction, so the grant persists) so the
        // delete-in-use guard has something to trip on.
        using var scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ISettableTenantContext>().SetBypass();
        var db = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();

        var user = User.Register($"member+{Guid.NewGuid():N}@wirehq.test", "Member", hasher.Hash(Password)).Value;
        user.VerifyEmail();
        db.Users.Add(user);
        db.Memberships.Add(Membership.CreateActive(organizationId, user.Id, [roleId]));
        await db.SaveChangesAsync(default);
    }

    private async Task<HttpClient> AuthenticateAdminAsync(Guid organizationId)
    {
        var email = $"roleadmin+{Guid.NewGuid():N}@wirehq.test";
        using (var scope = _factory.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<ISettableTenantContext>().SetBypass();
            var db = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
            var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();

            var user = User.Register(email, "Role Admin", hasher.Hash(Password)).Value;
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
        var email = $"roleowner+{Guid.NewGuid():N}@wirehq.test";
        (await client.PostAsJsonAsync("/api/v1/auth/register",
            new { email, password = Password, firstName = "Role", lastName = "Owner", acceptTerms = true })).EnsureSuccessStatusCode();
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
    private sealed record CreateDto(Guid Id);
    private sealed record RoleListDto(Guid Id, string Name, string? Description, bool IsSystem);
    private sealed record RoleDetailDto(Guid Id, string Name, string? Description, bool IsSystem, IReadOnlyList<Guid> PermissionIds);
    private sealed record PermissionDto(Guid Id, string Key, string Group, string Description);
}
