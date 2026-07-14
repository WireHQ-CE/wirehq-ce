using WireHQ.Domain.Common;

namespace WireHQ.Domain.Authorization;

/// <summary>
/// A fine-grained, stable permission from the global catalog (NOT tenant data). Code always
/// checks permissions by <see cref="Key"/> — never by role name — so roles can be reshaped
/// freely without touching authorization logic. Seeded from the constants in
/// WireHQ.Identity's permission catalog. (docs/04-security.md)
/// </summary>
public sealed class Permission : Entity
{
    // EF Core
    private Permission()
    {
    }

    public Permission(Guid id, string key, string group, string description)
        : base(id)
    {
        Key = key;
        Group = group;
        Description = description;
    }

    /// <summary>Namespaced action, e.g. <c>identity.users.invite</c>.</summary>
    public string Key { get; private set; } = null!;

    /// <summary>UI grouping, e.g. <c>Users</c>.</summary>
    public string Group { get; private set; } = null!;

    public string Description { get; private set; } = null!;
}
