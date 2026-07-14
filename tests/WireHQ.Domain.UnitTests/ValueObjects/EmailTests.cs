using FluentAssertions;
using WireHQ.Domain.ValueObjects;
using Xunit;

namespace WireHQ.Domain.UnitTests.ValueObjects;

public sealed class EmailTests
{
    [Theory]
    [InlineData("Ada@Example.com", "ada@example.com")]
    [InlineData("  user@wirehq.io  ", "user@wirehq.io")]
    public void Create_normalizes_valid_email(string input, string expected)
    {
        var result = Email.Create(input);

        result.IsSuccess.Should().BeTrue();
        result.Value.Value.Should().Be(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-an-email")]
    [InlineData("missing@domain")]
    public void Create_rejects_invalid_email(string input)
    {
        var result = Email.Create(input);

        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Emails_are_equal_by_value()
    {
        var a = Email.Create("user@wirehq.io").Value;
        var b = Email.Create("USER@wirehq.io").Value;

        a.Should().Be(b);
    }
}
