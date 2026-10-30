using FluentAssertions;
using WireHQ.Application.Features.Notifications;
using Xunit;

namespace WireHQ.Application.UnitTests.Notifications;

/// <summary>
/// The SMS phone-audience eligibility filter (docs/36-notifications.md §10, Slice B). A number is dispatched to ONLY
/// when the user has BOTH proven ownership (verified) AND consented (SmsAlerts) — ownership ≠ consent (docs/36 §4). A
/// wrong predicate here would text a non-consenting or unverified user, so it is unit-tested directly. (Org-scoping is
/// the caller's explicit OrganizationId predicate — the candidates fed here are already the rule's org.)
/// </summary>
public sealed class SmsAudienceTests
{
    [Fact]
    public void Includes_only_verified_and_consented_numbers()
    {
        var candidates = new[]
        {
            new SmsAudienceCandidate("+15005550001", PhoneVerified: true, SmsAlerts: true),   // ✓ both
            new SmsAudienceCandidate("+15005550002", PhoneVerified: true, SmsAlerts: false),  // ✗ verified, not consented
            new SmsAudienceCandidate("+15005550003", PhoneVerified: false, SmsAlerts: true),  // ✗ consented, not verified
            new SmsAudienceCandidate(null, PhoneVerified: true, SmsAlerts: true),             // ✗ no number
            new SmsAudienceCandidate("  ", PhoneVerified: true, SmsAlerts: true),             // ✗ blank number
        };

        var result = SmsAudience.Select(candidates);

        result.Should().ContainSingle().Which.Should().Be("+15005550001");
    }

    [Fact]
    public void Default_off_consent_excludes_a_user_with_no_preferences()
    {
        // A user with no preferences row is modelled as SmsAlerts:false (default OFF) → never included.
        var candidates = new[] { new SmsAudienceCandidate("+15005550009", PhoneVerified: true, SmsAlerts: false) };

        SmsAudience.Select(candidates).Should().BeEmpty();
    }

    [Fact]
    public void Deduplicates_the_same_number()
    {
        var candidates = new[]
        {
            new SmsAudienceCandidate("+15005550001", PhoneVerified: true, SmsAlerts: true),
            new SmsAudienceCandidate("+15005550001", PhoneVerified: true, SmsAlerts: true),
        };

        SmsAudience.Select(candidates).Should().ContainSingle();
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, false)]
    public void IsEligible_requires_both_facts(bool verified, bool consented, bool expected)
    {
        SmsAudience.IsEligible(new SmsAudienceCandidate("+15005550001", verified, consented)).Should().Be(expected);
    }
}
