namespace WireHQ.Domain.Notifications;

/// <summary>
/// Computes a digest rule's next flush cursor (docs/35-notifications.md §4.5, Wave 3). The cursor is <b>always</b> the
/// next fixed anchor boundary <b>strictly after</b> a given instant — <b>never</b> "cursor + one interval" — so that:
/// <list type="bullet">
/// <item>a freshly-created digest rule gets a concrete non-null first cursor (a null cursor never fires, because
/// <c>null &lt;= now</c> is UNKNOWN in SQL — finding null-cursor);</item>
/// <item>after any sender downtime the cursor jumps to the single next boundary rather than firing every tick to
/// "catch up" (finding cursor-advance / catch-up storm).</item>
/// </list>
/// Anchors are UTC-fixed: <see cref="DigestCadence.Daily"/> = the next UTC midnight; <see cref="DigestCadence.Weekly"/>
/// = the next Monday 00:00 UTC. <see cref="DigestCadence.Immediate"/> has no cursor (returns null).
/// </summary>
public static class DigestSchedule
{
    /// <summary>The next anchor boundary strictly after <paramref name="after"/>, or null for
    /// <see cref="DigestCadence.Immediate"/> (which has no digest cursor).</summary>
    public static DateTimeOffset? NextBoundary(DigestCadence cadence, DateTimeOffset after) => cadence switch
    {
        DigestCadence.Daily => NextMidnightUtc(after),
        DigestCadence.Weekly => NextMondayUtc(after),
        _ => null,
    };

    private static DateTimeOffset NextMidnightUtc(DateTimeOffset after)
    {
        // The midnight of the day AFTER 'after' — always strictly after any time on 'after's day.
        var day = after.UtcDateTime.Date.AddDays(1);
        return new DateTimeOffset(day, TimeSpan.Zero);
    }

    private static DateTimeOffset NextMondayUtc(DateTimeOffset after)
    {
        var day = after.UtcDateTime.Date;
        // Days until the next Monday; today-is-Monday maps to a full week (7), so the result is always >= day + 1 day,
        // hence strictly after any time on 'after's day.
        var daysUntilMonday = ((int)DayOfWeek.Monday - (int)day.DayOfWeek + 7) % 7;
        if (daysUntilMonday == 0)
        {
            daysUntilMonday = 7;
        }

        return new DateTimeOffset(day.AddDays(daysUntilMonday), TimeSpan.Zero);
    }
}
