using FluentAssertions;
using WireHQ.Domain.Identity;
using Xunit;

namespace WireHQ.Domain.UnitTests.Identity;

public sealed class EmailVerificationTokenTests
{
    [Fact]
    public void Issue_creates_an_active_unconsumed_token()
    {
        var now = DateTimeOffset.UtcNow;
        var token = EmailVerificationToken.Issue(Guid.CreateVersion7(), "hash", now.AddDays(3));

        token.IsActive(now).Should().BeTrue();
        token.UsedAtUtc.Should().BeNull();
    }

    [Fact]
    public void An_expired_token_is_not_active()
    {
        var now = DateTimeOffset.UtcNow;
        var token = EmailVerificationToken.Issue(Guid.CreateVersion7(), "hash", now.AddMinutes(-1));

        token.IsActive(now).Should().BeFalse();
    }

    [Fact]
    public void Consume_makes_it_inactive_and_is_idempotent()
    {
        var now = DateTimeOffset.UtcNow;
        var token = EmailVerificationToken.Issue(Guid.CreateVersion7(), "hash", now.AddDays(3));

        token.Consume(now);
        var firstUsedAt = token.UsedAtUtc;
        token.Consume(now.AddMinutes(5));

        token.IsActive(now).Should().BeFalse();
        token.UsedAtUtc.Should().Be(firstUsedAt, because: "consuming twice keeps the first timestamp");
    }
}
