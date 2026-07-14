using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WireHQ.Application.Authorization;
using WireHQ.Domain.Authorization;
using WireHQ.Infrastructure.Persistence;
using WireHQ.Infrastructure.Persistence.Seeding;
using Xunit;

namespace WireHQ.Api.IntegrationTests;

/// <summary>
/// The boot-time system-role permission reconciler (gap #3 / G-22). Proves that a permission added to
/// the catalog after an org was provisioned is re-granted to that org's existing Owner role, that the
/// pass is idempotent, and that non-system (custom) roles are left untouched. (docs/03-multi-tenancy.md)
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class SystemRolePermissionReconcilerTests(WireHqApiFactory factory)
{
    private readonly WireHqApiFactory _factory = factory;

    [Fact]
    public async Task Reconciler_regrants_missing_permissions_to_system_roles_only_and_is_idempotent()
    {
        var client = _factory.CreateClient();
        var orgId = await RegisterAsync(client, "Reconciler Tenant");

        // A permission a freshly-provisioned Owner already holds (a module permission in the catalog).
        const string targetKey = "wg.peers.manage";

        Guid ownerRoleId;
        Guid permissionId;
        Guid customRoleId;

        // ---- Setup: make the org look like it was provisioned BEFORE the permission existed (drop the
        //      Owner grant), and add a non-system custom role that legitimately lacks it. Direct tenant-table
        //      access with no request context ⇒ opt out of RLS like trusted infra. (ADR-027)
        using (var scope = _factory.CreateBypassScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            ownerRoleId = await db.Roles.IgnoreQueryFilters()
                .Where(r => r.OrganizationId == orgId && r.Name == SystemRoles.Owner && r.IsSystem)
                .Select(r => r.Id)
                .SingleAsync();

            permissionId = await db.Permissions
                .Where(p => p.Key == targetKey)
                .Select(p => p.Id)
                .SingleAsync();

            // Sanity: a fresh Owner starts WITH the permission.
            (await GrantExistsAsync(db, ownerRoleId, permissionId)).Should().BeTrue();

            // Drop it — now the Owner is "stale", like an org provisioned before the permission shipped.
            await db.Database.ExecuteSqlRawAsync(
                "DELETE FROM identity.role_permissions WHERE role_id = {0} AND permission_id = {1}",
                ownerRoleId, permissionId);
            (await GrantExistsAsync(db, ownerRoleId, permissionId)).Should().BeFalse();

            // A custom (non-system) role lacking the permission — it must stay that way.
            var custom = Role.Create(orgId, $"Custom {Guid.NewGuid():N}", isSystem: false).Value;
            db.Roles.Add(custom);
            await db.SaveChangesAsync(CancellationToken.None);
            customRoleId = custom.Id;
        }

        // ---- Act: run the reconciler twice to prove idempotency.
        await RunReconcilerAsync();
        await RunReconcilerAsync();

        // ---- Assert.
        using (var scope = _factory.CreateBypassScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var owner = await db.Roles.IgnoreQueryFilters().AsNoTracking().Include(r => r.Permissions).FirstAsync(r => r.Id == ownerRoleId);
            owner.Permissions.Count(p => p.PermissionId == permissionId).Should().Be(1,
                because: "the reconciler re-grants the catalogued permission exactly once (idempotent)");

            var custom = await db.Roles.IgnoreQueryFilters().AsNoTracking().Include(r => r.Permissions).FirstAsync(r => r.Id == customRoleId);
            custom.HasPermission(permissionId).Should().BeFalse(
                because: "only system roles are reconciled");
        }
    }

    private static async Task<bool> GrantExistsAsync(ApplicationDbContext db, Guid roleId, Guid permissionId)
    {
        var role = await db.Roles.IgnoreQueryFilters().AsNoTracking().Include(r => r.Permissions).FirstOrDefaultAsync(r => r.Id == roleId);
        return role?.HasPermission(permissionId) ?? false;
    }

    private async Task RunReconcilerAsync()
    {
        // The reconciler reads/writes system roles across all tenants — it relies on the RLS bypass that
        // SeedAsync sets at boot; replicate that here when invoking it directly. (ADR-027)
        using var scope = _factory.CreateBypassScope();
        var reconciler = scope.ServiceProvider.GetRequiredService<SystemRolePermissionReconciler>();
        await reconciler.ReconcileAsync(CancellationToken.None);
    }

    private static async Task<Guid> RegisterAsync(HttpClient client, string name)
    {
        var email = $"{name.Replace(' ', '.').ToLower()}+{Guid.NewGuid():N}@wirehq.test";
        var response = await client.PostAsJsonAsync("/api/v1/auth/register",
            new { email, password = "Sup3rSecret!!", firstName = name, lastName = "Test", acceptTerms = true });
        var body = (await response.Content.ReadFromJsonAsync<RegisterResponse>())!;
        return body.OrganizationId;
    }

    private sealed record RegisterResponse(Guid UserId, Guid OrganizationId, string OrganizationSlug);
}
