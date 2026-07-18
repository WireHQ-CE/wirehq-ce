using FluentAssertions;
using WireHQ.Domain.Notifications;
using Xunit;

namespace WireHQ.Domain.UnitTests.Notifications;

/// <summary>
/// The digest flush cursor is ALWAYS the next fixed anchor boundary strictly after a given instant — never
/// "cursor + interval" (docs/35 §4.5) — so a fresh rule gets a concrete cursor and downtime never triggers a
/// catch-up storm.
/// </summary>
public sealed class DigestScheduleTests
{
    [Fact]
    public void Immediate_has_no_cursor()
    {
        DigestSchedule.NextBoundary(DigestCadence.Immediate, DateTimeOffset.UtcNow).Should().BeNull();
    }

    [Theory]
    [InlineData("2026-07-16T10:00:00Z", "2026-07-17T00:00:00Z")] // midday → next UTC midnight
    [InlineData("2026-07-16T00:00:00Z", "2026-07-17T00:00:00Z")] // exactly midnight → strictly-after → next day
    [InlineData("2026-07-16T23:59:59Z", "2026-07-17T00:00:00Z")]
    public void Daily_is_the_next_utc_midnight_strictly_after(string after, string expected)
    {
        DigestSchedule.NextBoundary(DigestCadence.Daily, DateTimeOffset.Parse(after))
            .Should().Be(DateTimeOffset.Parse(expected));
    }

    [Theory]
    [InlineData("2026-07-16T10:00:00Z", "2026-07-20T00:00:00Z")] // Thu → next Mon (2026-07-20)
    [InlineData("2026-07-19T23:00:00Z", "2026-07-20T00:00:00Z")] // Sun → next Mon
    [InlineData("2026-07-20T00:00:00Z", "2026-07-27T00:00:00Z")] // Mon midnight → strictly-after → following Mon
    [InlineData("2026-07-20T09:00:00Z", "2026-07-27T00:00:00Z")] // Mon daytime → following Mon
    public void Weekly_is_the_next_monday_midnight_strictly_after(string after, string expected)
    {
        DigestSchedule.NextBoundary(DigestCadence.Weekly, DateTimeOffset.Parse(after))
            .Should().Be(DateTimeOffset.Parse(expected));
    }

    [Theory]
    [InlineData(DigestCadence.Daily)]
    [InlineData(DigestCadence.Weekly)]
    public void The_boundary_is_always_strictly_after_the_input(DigestCadence cadence)
    {
        var now = DateTimeOffset.UtcNow;
        DigestSchedule.NextBoundary(cadence, now)!.Value.Should().BeAfter(now);
    }
}
