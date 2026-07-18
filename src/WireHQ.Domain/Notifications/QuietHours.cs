namespace WireHQ.Domain.Notifications;

/// <summary>
/// A per-rule <b>quiet-hours</b> window (docs/35-notifications.md §5, Wave 3 — <c>notifications.routing</c>): a recurring
/// local-time span during which a rule's deliveries are <b>deferred, not dropped</b> — held until the window's end and
/// then sent. The window is two <see cref="TimeOnly"/> boundaries interpreted in an IANA time zone; a window whose
/// <c>end</c> is not after its <c>start</c> (e.g. <c>22:00</c>–<c>07:00</c>) <b>spans midnight</b>.
/// <para>
/// Enforced at <b>send time</b> against the current instant (never at capture): a delivery created before quiet hours
/// but that becomes due <i>during</i> them is still held, and one created during quiet hours whose window has since
/// passed goes straight out. The window travels on the <see cref="NotificationDelivery"/> (copied from the rule) so the
/// send path needs no rule load; a <see cref="NotificationDelivery"/> with no window (a test, or a free-core rule) is
/// never deferred.
/// </para>
/// </summary>
public static class QuietHours
{
    /// <summary>True when a complete quiet-hours window is present — a start, a <b>different</b> end, and a non-blank
    /// time zone. This is the marker a rule command folds into <c>notifications.routing</c> gating; time-zone
    /// <i>validity</i> (a resolvable IANA id) is a separate save-time check (<see cref="IsValidTimeZone"/>).</summary>
    public static bool IsConfigured(TimeOnly? start, TimeOnly? end, string? timeZoneId) =>
        start is { } s && end is { } e && s != e && !string.IsNullOrWhiteSpace(timeZoneId);

    /// <summary>True when <paramref name="timeZoneId"/> resolves to a real time zone (IANA ids on Linux/.NET, where the
    /// CE + SaaS run). Used to validate a rule's quiet-hours config at save so an unresolvable id can never be stored
    /// and then silently disable the feature at send time.</summary>
    public static bool IsValidTimeZone(string? timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId))
        {
            return false;
        }

        try
        {
            _ = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
            return true;
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return false;
        }
    }

    /// <summary>
    /// If <paramref name="nowUtc"/> falls inside the quiet window <c>[start, end)</c> (interpreted in
    /// <paramref name="timeZoneId"/>), returns the window's <b>end</b> as a UTC instant — the delivery should be deferred
    /// to it. Otherwise returns null (send now). A window with <c>end &lt;= start</c> spans midnight. Returns null for an
    /// unset (all-or-none) or zero-length (<c>start == end</c>) window.
    /// <para>
    /// <b>Fail-open:</b> any unresolvable time zone or a boundary that lands in a DST spring-forward gap returns null
    /// (send) rather than throwing — a mis-stored window must never silently black-hole a delivery.
    /// </para>
    /// </summary>
    public static DateTimeOffset? DeferUntil(TimeOnly? start, TimeOnly? end, string? timeZoneId, DateTimeOffset nowUtc)
    {
        if (start is not { } s || end is not { } e || string.IsNullOrWhiteSpace(timeZoneId) || s == e)
        {
            return null;
        }

        try
        {
            var tz = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
            var local = TimeZoneInfo.ConvertTime(nowUtc, tz);
            var localTime = TimeOnly.FromDateTime(local.DateTime);
            var localDate = DateOnly.FromDateTime(local.DateTime);

            var spansMidnight = e <= s;
            var isQuiet = spansMidnight
                ? localTime >= s || localTime < e   // e.g. 22:00–07:00 — the evening tail OR the early morning
                : localTime >= s && localTime < e;  // e.g. 01:00–06:00 — same calendar day
            if (!isQuiet)
            {
                return null;
            }

            // The window's end is the next local occurrence of `end`. Same-day window, or the early-morning portion of a
            // midnight-spanning window → today's `end`; the evening portion of a midnight-spanning window → tomorrow's.
            var endDate = spansMidnight && localTime >= s ? localDate.AddDays(1) : localDate;
            var endLocal = DateTime.SpecifyKind(endDate.ToDateTime(e), DateTimeKind.Unspecified);
            return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(endLocal, tz), TimeSpan.Zero);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException)
        {
            return null;
        }
    }
}
