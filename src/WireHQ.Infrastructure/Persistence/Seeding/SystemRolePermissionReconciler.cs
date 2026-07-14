using Microsoft.EntityFrameworkCore;
using WireHQ.Application.Authorization;

namespace WireHQ.Infrastructure.Persistence.Seeding;

/// <summary>
/// Reconciles every organization's SYSTEM roles against the current permission catalog, granting any
/// permissions they are missing. Runs on every startup AFTER <see cref="PermissionSeeder"/> so that
/// permissions introduced in a release (including new module permissions) reach EXISTING tenants — not
/// only freshly-provisioned ones, which
/// <see cref="WireHQ.Application.Organizations.OrganizationProvisioner"/> already grants in full.
/// Without this, an existing org's Owner silently lacks a newly-shipped permission and the gated
/// feature stays hidden until the org is re-provisioned (gap #3 / G-22).
///
/// Grants are written SET-BASED with idempotent SQL rather than via the domain <c>Role.Grant</c> +
/// <c>SaveChanges</c>: <c>RolePermission</c> is an <c>OwnsMany</c> owned collection, and appending to
/// an already-persisted owner emits a 0-row UPDATE (G-26). The SQL is additive only — it never
/// revokes, so an operator/customer never loses an existing grant (system roles are immutable by
/// design). Idempotent via the primary key + the <c>NOT EXISTS</c> guard: a no-op when nothing is
/// missing.
/// </summary>
public sealed class SystemRolePermissionReconciler(ApplicationDbContext dbContext) : IDataSeeder
{
    // Right after PermissionSeeder: re-grants newly-catalogued permissions to EXISTING orgs' system
    // roles (gap #3 / G-22), so a release that adds a permission reaches established tenants too.
    public int Order => 20;

    // The Owner system role always holds the ENTIRE catalog (matches OrganizationProvisioner).
    private const string GrantOwnerCatalogSql =
        @"INSERT INTO identity.role_permissions (role_id, permission_id)
          SELECT r.id, p.id
          FROM identity.roles r
          CROSS JOIN identity.permissions p
          WHERE r.is_system = TRUE AND r.name = {0}
            AND NOT EXISTS (SELECT 1 FROM identity.role_permissions rp
                            WHERE rp.role_id = r.id AND rp.permission_id = p.id);";

    // Every other system role holds its canonical, fixed permission set (matched by permission key).
    private const string GrantRoleKeysSql =
        @"INSERT INTO identity.role_permissions (role_id, permission_id)
          SELECT r.id, p.id
          FROM identity.roles r
          JOIN identity.permissions p ON p.""key"" = ANY({1})
          WHERE r.is_system = TRUE AND r.name = {0}
            AND NOT EXISTS (SELECT 1 FROM identity.role_permissions rp
                            WHERE rp.role_id = r.id AND rp.permission_id = p.id);";

    public Task SeedAsync(CancellationToken cancellationToken = default) => ReconcileAsync(cancellationToken);

    public async Task ReconcileAsync(CancellationToken cancellationToken = default)
    {
        // Owner = the whole catalog (including module permissions contributed beyond SystemRoles).
        await dbContext.Database.ExecuteSqlRawAsync(
            GrantOwnerCatalogSql, new object[] { SystemRoles.Owner }, cancellationToken);

        // The remaining system roles get exactly the keys their canonical definition lists.
        foreach (var (roleName, permissionKeys) in SystemRoles.Definitions)
        {
            if (roleName == SystemRoles.Owner)
            {
                continue;
            }

            await dbContext.Database.ExecuteSqlRawAsync(
                GrantRoleKeysSql, new object[] { roleName, permissionKeys.ToArray() }, cancellationToken);
        }
    }
}
