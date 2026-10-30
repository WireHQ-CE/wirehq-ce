using FluentAssertions;
using WireHQ.Domain.Identity;
using Xunit;

namespace WireHQ.Domain.UnitTests.Identity;

/// <summary>
/// The phone-ownership challenge state machine (docs/36-notifications.md §5): constant-time code check
/// (<c>FixedTimeEquals</c> on the salted hash), a hard attempt cap that BURNS the challenge, a single-use consume, and
/// a ~10-minute expiry. Every non-success path is one outcome the handler collapses to a single generic error.
/// </summary>
public sealed class PhoneVerificationTests
{
    private static readonly Guid Org = Guid.NewGuid();
    private static readonly Guid UserId = Guid.NewGuid();
    private const string Phone = "+14155550123";
    private static readonly DateTimeOffset Now = new(2026, 7, 19, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Correct_code_verifies_and_consumes_single_use()
    {
        var v = PhoneVerification.Issue(Org, UserId, Phone, "123456", Now);

        var outcome = v.Confirm(PhoneVerification.HashCode(v.Id, "123456"), Now.AddMinutes(1));

        outcome.Should().Be(PhoneVerificationOutcome.Verified);
        v.ConsumedAtUtc.Should().NotBeNull("a verified challenge is single-use");

        // Single-use: a second confirm (even with the right code) is rejected.
        v.Confirm(PhoneVerification.HashCode(v.Id, "123456"), Now.AddMinutes(1))
            .Should().Be(PhoneVerificationOutcome.Invalidated);
    }

    [Fact]
    public void The_plaintext_code_is_never_stored()
    {
        var v = PhoneVerification.Issue(Org, UserId, Phone, "654321", Now);

        v.CodeHash.Should().NotContain("654321");
        v.CodeHash.Should().Be(PhoneVerification.HashCode(v.Id, "654321"));
    }

    [Fact]
    public void Hash_is_salted_per_challenge_so_the_same_code_hashes_differently()
    {
        var a = PhoneVerification.Issue(Org, UserId, Phone, "111111", Now);
        var b = PhoneVerification.Issue(Org, UserId, Phone, "111111", Now);

        a.CodeHash.Should().NotBe(b.CodeHash, "the row id is the salt");
    }

    [Fact]
    public void Wrong_code_is_a_mismatch_and_counts_an_attempt()
    {
        var v = PhoneVerification.Issue(Org, UserId, Phone, "123456", Now);

        v.Confirm(PhoneVerification.HashCode(v.Id, "000000"), Now).Should().Be(PhoneVerificationOutcome.Mismatch);
        v.Attempts.Should().Be(1);
        v.ConsumedAtUtc.Should().BeNull();
    }

    [Fact]
    public void Fifth_wrong_attempt_burns_the_challenge_then_it_is_locked()
    {
        var v = PhoneVerification.Issue(Org, UserId, Phone, "123456", Now);

        for (var i = 0; i < PhoneVerification.MaxAttempts; i++)
        {
            v.Confirm(PhoneVerification.HashCode(v.Id, "000000"), Now).Should().Be(PhoneVerificationOutcome.Mismatch);
        }

        v.Attempts.Should().Be(PhoneVerification.MaxAttempts);
        v.ConsumedAtUtc.Should().NotBeNull("the challenge is burned after the attempt cap");

        // Even the CORRECT code no longer works — the challenge was invalidated.
        v.Confirm(PhoneVerification.HashCode(v.Id, "123456"), Now).Should().Be(PhoneVerificationOutcome.Invalidated);
    }

    [Fact]
    public void Expired_code_is_rejected_and_burned()
    {
        var v = PhoneVerification.Issue(Org, UserId, Phone, "123456", Now);

        var outcome = v.Confirm(PhoneVerification.HashCode(v.Id, "123456"), Now.AddMinutes(PhoneVerification.ExpiryMinutes + 1));

        outcome.Should().Be(PhoneVerificationOutcome.Expired);
        v.ConsumedAtUtc.Should().NotBeNull();
    }

    [Fact]
    public void A_malformed_presented_hash_never_throws_and_is_a_mismatch()
    {
        var v = PhoneVerification.Issue(Org, UserId, Phone, "123456", Now);

        // A non-hex / wrong-length presented hash must be handled constant-time-safe, never an exception (no oracle).
        var act = () => v.Confirm("not-a-hex-hash", Now);

        act.Should().NotThrow();
        v.Confirm("abcd", Now).Should().Be(PhoneVerificationOutcome.Mismatch);
    }

    [Fact]
    public void IsActive_reflects_unspent_and_unexpired()
    {
        var v = PhoneVerification.Issue(Org, UserId, Phone, "123456", Now);

        v.IsActive(Now.AddMinutes(1)).Should().BeTrue();
        v.IsActive(Now.AddMinutes(PhoneVerification.ExpiryMinutes + 1)).Should().BeFalse("expired");

        v.Supersede(Now);
        v.IsActive(Now.AddMinutes(1)).Should().BeFalse("superseded/consumed");
    }
}
