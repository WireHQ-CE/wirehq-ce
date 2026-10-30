using FluentAssertions;
using WireHQ.Domain.Notifications;
using Xunit;

namespace WireHQ.Domain.UnitTests.Notifications;

/// <summary>The append-only SMS suppression ledger (docs/36 §4.3, Slice D): STOP appends an active row; START clears it
/// (a state transition, never a delete); "currently suppressed" is an active (uncleared) row.</summary>
public sealed class SmsOptOutTests
{
    private static readonly Guid Org = Guid.CreateVersion7();
    private static readonly DateTimeOffset T0 = DateTimeOffset.UnixEpoch;

    [Fact]
    public void Suppress_creates_an_active_row()
    {
        var row = SmsOptOut.Suppress(Org, "+447700900001", "+15550001111", SmsOptOutSource.StopKeyword, "STOP", reason: null, T0);

        row.OrganizationId.Should().Be(Org);
        row.PhoneE164.Should().Be("+447700900001");
        row.Sender.Should().Be("+15550001111");
        row.Source.Should().Be(SmsOptOutSource.StopKeyword);
        row.Keyword.Should().Be("STOP");
        row.SuppressedAtUtc.Should().Be(T0);
        row.ClearedAtUtc.Should().BeNull();
        row.IsActive.Should().BeTrue("a fresh STOP row is an active suppression");
    }

    [Fact]
    public void Clear_transitions_the_row_to_inactive_without_deleting_it()
    {
        var row = SmsOptOut.Suppress(Org, "+447700900001", null, SmsOptOutSource.StopKeyword, "STOP", reason: null, T0);
        var later = T0.AddHours(1);

        row.Clear("START", later);

        row.ClearedAtUtc.Should().Be(later);
        row.IsActive.Should().BeFalse("a cleared row is no longer an active suppression");
        row.SuppressedAtUtc.Should().Be(T0, "the original suppression instant is preserved (the ledger is append-only)");

        // This previously asserted Keyword == "START" — i.e. that clearing OVERWRITES the keyword that caused the
        // opt-out. That encoded a defect: the row would then report UNSTOP/START as the reason the subscriber had
        // opted out, a false statement in a ledger whose whole justification is durable TCPA/carrier proof. Both
        // facts are now kept, separately.
        row.Keyword.Should().Be("STOP", "the row must still record why the subscriber opted OUT");
        row.ClearedKeyword.Should().Be("START", "and, separately, which keyword cleared it");
    }

    [Fact]
    public void Clear_is_idempotent_and_keeps_the_first_clear_instant()
    {
        var row = SmsOptOut.Suppress(Org, "+447700900001", null, SmsOptOutSource.StopKeyword, "STOP", reason: null, T0);
        var first = T0.AddHours(1);
        var second = T0.AddHours(2);

        row.Clear("START", first);
        row.Clear("YES", second);

        row.ClearedAtUtc.Should().Be(first, "a second clear is a no-op — the row was already cleared");
    }

    [Fact]
    public void An_operator_recorded_row_carries_its_reason_and_no_keyword()
    {
        // W3: a revocation that arrived by phone/email/ticket. FCC 47 CFR 64.1200(a)(10) — revocation by any reasonable
        // means is valid — so it must be recordable, and the ledger has to say how it reached the operator. Before W3
        // the Reason column had no writer at all.
        var row = SmsOptOut.Suppress(
            Org, "+447700900001", "+15550001111", SmsOptOutSource.OperatorRequest, keyword: null,
            reason: "phoned support, ticket 1234", T0);

        row.Source.Should().Be(SmsOptOutSource.OperatorRequest);
        row.Reason.Should().Be("phoned support, ticket 1234");
        row.Keyword.Should().BeNull("nobody texted anything — there is no inbound keyword to record");
        row.IsActive.Should().BeTrue();
    }

    [Fact]
    public void A_carrier_error_row_is_an_active_suppression_too()
    {
        var row = SmsOptOut.Suppress(Org, "+447700900001", "+15550001111", SmsOptOutSource.CarrierError, keyword: null, reason: null, T0);

        row.Source.Should().Be(SmsOptOutSource.CarrierError);
        row.IsActive.Should().BeTrue();
        row.Keyword.Should().BeNull();
    }
}
