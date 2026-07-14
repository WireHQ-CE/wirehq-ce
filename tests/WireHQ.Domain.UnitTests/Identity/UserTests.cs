using FluentAssertions;
using WireHQ.Domain.Identity;
using Xunit;

namespace WireHQ.Domain.UnitTests.Identity;

public sealed class UserTests
{
    private static User NewUser() => User.Register("ada@wirehq.io", "Ada Lovelace", "hash").Value;

    [Fact]
    public void Register_creates_pending_user_and_raises_event()
    {
        var result = User.Register("ada@wirehq.io", "Ada Lovelace", "hash");

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be(UserStatus.PendingVerification);
        result.Value.DomainEvents.Should().ContainSingle(e => e is UserRegistered);
    }

    [Fact]
    public void VerifyEmail_activates_a_pending_user()
    {
        var user = NewUser();

        user.VerifyEmail();

        user.EmailVerified.Should().BeTrue();
        user.Status.Should().Be(UserStatus.Active);
    }

    [Fact]
    public void Repeated_failed_sign_ins_lock_the_account()
    {
        var user = NewUser();
        var now = DateTimeOffset.UtcNow;

        for (var i = 0; i < 5; i++)
        {
            user.RegisterFailedSignIn(now);
        }

        user.Status.Should().Be(UserStatus.Locked);
        user.IsLockedOut(now).Should().BeTrue();
    }

    [Fact]
    public void Successful_sign_in_clears_lockout_state()
    {
        var user = NewUser();
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < 5; i++)
        {
            user.RegisterFailedSignIn(now);
        }

        user.RegisterSuccessfulSignIn(now.AddHours(1));

        user.FailedSignInAttempts.Should().Be(0);
        user.IsLockedOut(now.AddHours(1)).Should().BeFalse();
    }

    [Fact]
    public void Changing_password_rotates_the_security_stamp()
    {
        var user = NewUser();
        var before = user.SecurityStamp;

        user.ChangePassword("new-hash");

        user.SecurityStamp.Should().NotBe(before);
    }
}
