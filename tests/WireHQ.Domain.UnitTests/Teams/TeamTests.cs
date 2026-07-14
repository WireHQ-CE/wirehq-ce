using FluentAssertions;
using WireHQ.Domain.Teams;
using Xunit;

namespace WireHQ.Domain.UnitTests.Teams;

public sealed class TeamTests
{
    private static readonly Guid OrgId = Guid.CreateVersion7();

    private static Team NewTeam(string name = "Platform Engineering") => Team.Create(OrgId, name).Value;

    [Fact]
    public void Create_with_a_valid_name_succeeds_and_derives_a_slug()
    {
        var result = Team.Create(OrgId, "Platform Engineering");

        result.IsSuccess.Should().BeTrue();
        result.Value.Name.Should().Be("Platform Engineering");
        result.Value.Slug.Value.Should().NotBeNullOrWhiteSpace();
        result.Value.OrganizationId.Should().Be(OrgId);
        result.Value.Members.Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_with_a_blank_name_fails(string name)
    {
        var result = Team.Create(OrgId, name);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(TeamErrors.InvalidName);
    }

    [Fact]
    public void Create_with_an_overlong_name_fails()
    {
        var result = Team.Create(OrgId, new string('x', Team.MaxNameLength + 1));

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(TeamErrors.InvalidName);
    }

    [Fact]
    public void Rename_updates_the_name()
    {
        var team = NewTeam();

        var result = team.Rename("Security");

        result.IsSuccess.Should().BeTrue();
        team.Name.Should().Be("Security");
    }

    [Fact]
    public void Rename_to_a_blank_name_fails()
    {
        var team = NewTeam();

        team.Rename("  ").IsFailure.Should().BeTrue();
        team.Name.Should().Be("Platform Engineering");
    }

    [Fact]
    public void AddMember_adds_the_member_and_is_idempotent()
    {
        var team = NewTeam();
        var membershipId = Guid.CreateVersion7();

        team.AddMember(membershipId);
        team.AddMember(membershipId); // re-adding is a no-op

        team.Members.Should().ContainSingle(m => m.MembershipId == membershipId);
    }

    [Fact]
    public void RemoveMember_removes_an_existing_member()
    {
        var team = NewTeam();
        var membershipId = Guid.CreateVersion7();
        team.AddMember(membershipId);

        var result = team.RemoveMember(membershipId);

        result.IsSuccess.Should().BeTrue();
        team.Members.Should().BeEmpty();
    }

    [Fact]
    public void RemoveMember_for_a_non_member_fails()
    {
        var team = NewTeam();

        var result = team.RemoveMember(Guid.CreateVersion7());

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(TeamErrors.MemberNotFound);
    }
}
