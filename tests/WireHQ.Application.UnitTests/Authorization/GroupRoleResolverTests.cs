using FluentAssertions;
using WireHQ.Application.Authorization;
using Xunit;

namespace WireHQ.Application.UnitTests.Authorization;

public sealed class GroupRoleResolverTests
{
    private static readonly Guid Admins = Guid.CreateVersion7();
    private static readonly Guid Members = Guid.CreateVersion7();
    private static readonly Guid Default = Guid.CreateVersion7();

    private static readonly GroupRoleRule[] Rules =
    [
        new("CN=VPN-Admins,DC=acme,DC=com", Admins),
        new("CN=VPN-Users,DC=acme,DC=com", Members),
    ];

    [Fact]
    public void First_matching_rule_in_order_wins()
    {
        var role = GroupRoleResolver.Resolve(
            ["CN=VPN-Users,DC=acme,DC=com", "CN=VPN-Admins,DC=acme,DC=com"], Rules, Default);

        role.Should().Be(Admins, because: "the Admins rule is earlier in the ordered list");
    }

    [Fact]
    public void Matching_is_case_insensitive()
    {
        var role = GroupRoleResolver.Resolve(["cn=vpn-admins,dc=acme,dc=com"], Rules, Default);

        role.Should().Be(Admins);
    }

    [Fact]
    public void No_group_match_falls_back_to_the_default_role()
    {
        var role = GroupRoleResolver.Resolve(["CN=Other,DC=acme,DC=com"], Rules, Default);

        role.Should().Be(Default);
    }

    [Fact]
    public void No_groups_falls_back_to_the_default_role()
    {
        GroupRoleResolver.Resolve([], Rules, Default).Should().Be(Default);
    }

    [Fact]
    public void No_match_and_no_default_resolves_to_null()
    {
        GroupRoleResolver.Resolve(["CN=Other,DC=acme,DC=com"], Rules, defaultRoleId: null).Should().BeNull();
    }
}
