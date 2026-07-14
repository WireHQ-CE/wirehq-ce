using FluentAssertions;
using WireHQ.Domain.Authorization;
using Xunit;

namespace WireHQ.Domain.UnitTests.Authorization;

public sealed class RoleTests
{
    private static readonly Guid OrgId = Guid.CreateVersion7();

    [Fact]
    public void Create_makes_a_custom_role_by_default()
    {
        var role = Role.Create(OrgId, "Network Operator").Value;

        role.IsSystem.Should().BeFalse();
        role.Name.Should().Be("Network Operator");
        role.Permissions.Should().BeEmpty();
    }

    [Fact]
    public void Create_rejects_a_blank_or_overlong_name()
    {
        Role.Create(OrgId, " ").Error.Should().Be(RoleErrors.InvalidName);
        Role.Create(OrgId, new string('x', Role.MaxNameLength + 1)).Error.Should().Be(RoleErrors.InvalidName);
    }

    [Fact]
    public void Rename_updates_the_name_and_validates()
    {
        var role = Role.Create(OrgId, "Old").Value;

        role.Rename("New").IsSuccess.Should().BeTrue();
        role.Name.Should().Be("New");
        role.Rename(" ").Error.Should().Be(RoleErrors.InvalidName);
    }

    [Fact]
    public void SetPermissions_replaces_the_set_and_is_idempotent()
    {
        var role = Role.Create(OrgId, "Ops").Value;
        var a = Guid.CreateVersion7();
        var b = Guid.CreateVersion7();
        var c = Guid.CreateVersion7();

        role.SetPermissions([a, b]);
        role.Permissions.Select(p => p.PermissionId).Should().BeEquivalentTo([a, b]);

        // Replace: drop a, keep b, add c — and de-duplicate.
        role.SetPermissions([b, c, c]);
        role.Permissions.Select(p => p.PermissionId).Should().BeEquivalentTo([b, c]);

        role.HasPermission(a).Should().BeFalse();
        role.HasPermission(c).Should().BeTrue();
    }
}
