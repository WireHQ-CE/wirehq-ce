using WireHQ.Domain.Common;
using WireHQ.Shared.Results;

namespace WireHQ.Domain.Authorization;

/// <summary>
/// A named bundle of permissions, scoped to one organization. System roles (Owner, Admin,
/// Member, Billing, Auditor) are seeded per tenant; Enterprise tenants may compose custom
/// roles. Two orgs' "Admin" can legitimately differ — roles are tenant data.
/// </summary>
public sealed class Role : AggregateRoot, ITenantOwned, IAuditable
{
    public const int MaxNameLength = 64;

    private readonly List<RolePermission> _permissions = [];

    // EF Core
    private Role()
    {
    }

    private Role(Guid id, Guid organizationId, string name, bool isSystem)
        : base(id)
    {
        OrganizationId = organizationId;
        Name = name;
        IsSystem = isSystem;
    }

    public Guid OrganizationId { get; private set; }

    public string Name { get; private set; } = null!;

    public string? Description { get; private set; }

    /// <summary>System roles cannot be deleted and have a protected permission set.</summary>
    public bool IsSystem { get; private set; }

    public IReadOnlyCollection<RolePermission> Permissions => _permissions.AsReadOnly();

    public DateTimeOffset CreatedAtUtc { get; private set; }
    public Guid? CreatedBy { get; private set; }
    public DateTimeOffset? UpdatedAtUtc { get; private set; }
    public Guid? UpdatedBy { get; private set; }

    public static Result<Role> Create(Guid organizationId, string name, bool isSystem = false)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > MaxNameLength)
        {
            return RoleErrors.InvalidName;
        }

        return new Role(Guid.CreateVersion7(), organizationId, name.Trim(), isSystem);
    }

    /// <summary>Rename the role (custom roles only — the caller guards <see cref="IsSystem"/>).</summary>
    public Result Rename(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > MaxNameLength)
        {
            return RoleErrors.InvalidName;
        }

        Name = name.Trim();
        return Result.Success();
    }

    public void Grant(Guid permissionId)
    {
        if (_permissions.All(p => p.PermissionId != permissionId))
        {
            _permissions.Add(new RolePermission(Id, permissionId));
        }
    }

    public void Revoke(Guid permissionId) =>
        _permissions.RemoveAll(p => p.PermissionId == permissionId);

    /// <summary>Replace the granted permission set wholesale (the role editor's checkbox semantics) by diffing
    /// against the current grants — so EF emits only the actual inserts/deletes.</summary>
    public void SetPermissions(IReadOnlyCollection<Guid> permissionIds)
    {
        var desired = permissionIds.ToHashSet();
        foreach (var removed in _permissions.Where(p => !desired.Contains(p.PermissionId)).ToList())
        {
            _permissions.Remove(removed);
        }

        foreach (var id in desired)
        {
            Grant(id);
        }
    }

    public void Describe(string? description) => Description = description?.Trim();

    public bool HasPermission(Guid permissionId) => _permissions.Any(p => p.PermissionId == permissionId);
}

/// <summary>Join entity: a permission granted to a role.</summary>
public sealed class RolePermission
{
    // EF Core
    private RolePermission()
    {
    }

    public RolePermission(Guid roleId, Guid permissionId)
    {
        RoleId = roleId;
        PermissionId = permissionId;
    }

    public Guid RoleId { get; private set; }

    public Guid PermissionId { get; private set; }
}

public static class RoleErrors
{
    public static readonly Error InvalidName = Error.Validation("role.invalid_name", "Role name is required and must be 64 characters or fewer.");
    public static readonly Error NotFound = Error.NotFound("role.not_found", "Role was not found.");
    public static readonly Error SystemRoleImmutable = Error.Conflict("role.system_immutable", "System roles cannot be modified or deleted.");
    public static readonly Error NameTaken = Error.Conflict("role.name_taken", "A role with this name already exists in this organization.");
    public static readonly Error UnknownPermission = Error.Validation("role.unknown_permission", "One or more selected permissions do not exist.");
    public static readonly Error PermissionNotGrantable = Error.Forbidden("role.permission_not_grantable", "You can only grant permissions you hold yourself.");
    public static readonly Error InUse = Error.Conflict("role.in_use", "This role is assigned to one or more members — reassign them before deleting it.");
}
