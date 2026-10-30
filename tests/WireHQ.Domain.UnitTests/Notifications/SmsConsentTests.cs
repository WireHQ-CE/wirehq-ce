using FluentAssertions;
using WireHQ.Domain.Notifications;
using Xunit;

namespace WireHQ.Domain.UnitTests.Notifications;

/// <summary>Per-(org, user) SMS consent (docs/36 §4.1, D-3): a queryable current-state record whose <c>Set</c> reports
/// real changes so the handler audits only genuine toggles.</summary>
public sealed class SmsConsentTests
{
    private static readonly Guid Org = Guid.CreateVersion7();
    private static readonly Guid User = Guid.CreateVersion7();
    private static readonly DateTimeOffset T0 = DateTimeOffset.UnixEpoch;
    private const string V = "2026-07-19";

    [Fact]
    public void Create_opted_in_records_the_consent_instant_and_disclosure_version()
    {
        var consent = SmsConsent.Create(Org, User, consented: true, V, T0);

        consent.OrganizationId.Should().Be(Org);
        consent.UserId.Should().Be(User);
        consent.Consented.Should().BeTrue();
        consent.ConsentedAtUtc.Should().Be(T0);
        consent.RevokedAtUtc.Should().BeNull();
        consent.DisclosureVersion.Should().Be(V);
    }

    [Fact]
    public void Set_to_revoked_records_the_revocation_and_reports_the_change()
    {
        var consent = SmsConsent.Create(Org, User, consented: true, V, T0);
        var later = T0.AddDays(1);

        consent.Set(consented: false, V, later).Should().BeTrue("the value changed");
        consent.Consented.Should().BeFalse();
        consent.RevokedAtUtc.Should().Be(later);
        consent.ConsentedAtUtc.Should().Be(T0, "the prior opt-in instant is retained");
    }

    [Fact]
    public void Set_to_the_same_value_is_a_no_op()
    {
        var consent = SmsConsent.Create(Org, User, consented: true, V, T0);

        consent.Set(consented: true, V, T0.AddDays(1)).Should().BeFalse("no real change ⇒ no audit event");
        consent.ConsentedAtUtc.Should().Be(T0);
    }

    [Fact]
    public void Re_opting_in_clears_the_revocation_and_refreshes_the_consent_instant()
    {
        var consent = SmsConsent.Create(Org, User, consented: true, V, T0);
        consent.Set(consented: false, V, T0.AddDays(1));

        var reoptIn = T0.AddDays(2);
        consent.Set(consented: true, "2026-08-01", reoptIn).Should().BeTrue();

        consent.Consented.Should().BeTrue();
        consent.ConsentedAtUtc.Should().Be(reoptIn);
        consent.RevokedAtUtc.Should().BeNull();
        consent.DisclosureVersion.Should().Be("2026-08-01", "the version agreed to at the new opt-in is recorded");
    }
}
