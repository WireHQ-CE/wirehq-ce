using FluentAssertions;
using WireHQ.Domain.Notifications;
using Xunit;

namespace WireHQ.Domain.UnitTests.Notifications;

/// <summary>
/// Quiet hours DEFER a rule's deliveries to the window's end rather than dropping them, evaluated at send time against
/// the current instant in the rule's IANA time zone (docs/35 §5, Wave 3). The window is end-exclusive and may span
/// midnight; anything unresolvable fails OPEN (send) so a mis-stored window never black-holes a delivery.
/// </summary>
public sealed class QuietHoursTests
{
    [Theory]
    // Same-day window 01:00–06:00 (UTC): inside defers to the end; the boundaries are [start, end).
    [InlineData("01:00", "06:00", "Etc/UTC", "2026-07-16T03:00:00Z", "2026-07-16T06:00:00Z")]
    [InlineData("01:00", "06:00", "Etc/UTC", "2026-07-16T01:00:00Z", "2026-07-16T06:00:00Z")] // start is inclusive
    [InlineData("01:00", "06:00", "Etc/UTC", "2026-07-16T06:00:00Z", null)]                    // end is exclusive
    [InlineData("01:00", "06:00", "Etc/UTC", "2026-07-16T00:30:00Z", null)]                    // before the window
    [InlineData("01:00", "06:00", "Etc/UTC", "2026-07-16T09:00:00Z", null)]                    // after the window
    // Window spanning midnight 22:00–07:00 (UTC): the evening tail defers to TOMORROW's end, the morning to today's.
    [InlineData("22:00", "07:00", "Etc/UTC", "2026-07-16T23:00:00Z", "2026-07-17T07:00:00Z")]  // evening
    [InlineData("22:00", "07:00", "Etc/UTC", "2026-07-16T22:00:00Z", "2026-07-17T07:00:00Z")]  // start is inclusive
    [InlineData("22:00", "07:00", "Etc/UTC", "2026-07-16T05:00:00Z", "2026-07-16T07:00:00Z")]  // early morning
    [InlineData("22:00", "07:00", "Etc/UTC", "2026-07-16T07:00:00Z", null)]                    // end is exclusive
    [InlineData("22:00", "07:00", "Etc/UTC", "2026-07-16T12:00:00Z", null)]                    // midday
    // Time-zone aware: 22:00–07:00 New York local. 03:00Z = 23:00 EDT (UTC-4, summer) → evening → next 07:00 EDT = 11:00Z.
    [InlineData("22:00", "07:00", "America/New_York", "2026-07-16T03:00:00Z", "2026-07-16T11:00:00Z")]
    [InlineData("22:00", "07:00", "America/New_York", "2026-07-16T18:00:00Z", null)]           // 14:00 EDT — not quiet
    public void DeferUntil_holds_only_during_the_window(string start, string end, string tz, string nowIso, string? expectedIso)
    {
        var result = QuietHours.DeferUntil(TimeOnly.Parse(start), TimeOnly.Parse(end), tz, DateTimeOffset.Parse(nowIso));

        if (expectedIso is null)
        {
            result.Should().BeNull();
        }
        else
        {
            result.Should().Be(DateTimeOffset.Parse(expectedIso));
        }
    }

    [Theory]
    [InlineData(null, "06:00", "Etc/UTC")]   // no start
    [InlineData("01:00", null, "Etc/UTC")]   // no end
    [InlineData("01:00", "06:00", null)]     // no time zone
    [InlineData("03:00", "03:00", "Etc/UTC")] // zero-length window
    public void DeferUntil_is_null_for_an_incomplete_or_empty_window(string? start, string? end, string? tz)
    {
        var s = start is null ? (TimeOnly?)null : TimeOnly.Parse(start);
        var e = end is null ? (TimeOnly?)null : TimeOnly.Parse(end);

        QuietHours.DeferUntil(s, e, tz, DateTimeOffset.Parse("2026-07-16T03:00:00Z")).Should().BeNull();
    }

    [Fact]
    public void DeferUntil_fails_open_on_an_unresolvable_time_zone()
    {
        // A window that would otherwise match, but the stored zone can't be resolved: send (null), never black-hole.
        QuietHours.DeferUntil(new TimeOnly(1, 0), new TimeOnly(6, 0), "Not/ARealZone", DateTimeOffset.Parse("2026-07-16T03:00:00Z"))
            .Should().BeNull();
    }

    [Theory]
    [InlineData("America/New_York", true)]
    [InlineData("Etc/UTC", true)]
    [InlineData("Not/ARealZone", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsValidTimeZone_resolves_iana_ids(string? tz, bool expected)
    {
        QuietHours.IsValidTimeZone(tz).Should().Be(expected);
    }

    [Theory]
    [InlineData("01:00", "06:00", "Etc/UTC", true)]
    [InlineData("22:00", "07:00", "Etc/UTC", true)]  // spanning midnight is still "configured"
    [InlineData(null, "06:00", "Etc/UTC", false)]
    [InlineData("01:00", null, "Etc/UTC", false)]
    [InlineData("01:00", "06:00", "", false)]
    [InlineData("03:00", "03:00", "Etc/UTC", false)] // zero-length
    public void IsConfigured_requires_a_complete_nonzero_window(string? start, string? end, string? tz, bool expected)
    {
        var s = start is null ? (TimeOnly?)null : TimeOnly.Parse(start);
        var e = end is null ? (TimeOnly?)null : TimeOnly.Parse(end);

        QuietHours.IsConfigured(s, e, tz).Should().Be(expected);
    }
}
