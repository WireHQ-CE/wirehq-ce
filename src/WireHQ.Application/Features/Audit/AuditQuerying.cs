using System.Text;
using WireHQ.Domain.Auditing;
using WireHQ.Shared.Primitives;

namespace WireHQ.Application.Features.Audit;

/// <summary>
/// Shared keyset-pagination + filtering for the audit reads — used by BOTH the tenant-scoped
/// <c>ListAuditLogsQuery</c> and the cross-tenant platform read, so the two stay consistent. The order is
/// always <c>(occurred_at_utc DESC, id DESC)</c>; the cursor carries the last-seen row's key. (docs/15 §5)
/// </summary>
public static class AuditQuerying
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 200;

    public static int ClampPageSize(int pageSize) =>
        pageSize < 1 ? DefaultPageSize : pageSize > MaxPageSize ? MaxPageSize : pageSize;

    /// <summary>Apply the rich filter set (date range, action, category, actor, target, outcome, free-text).</summary>
    public static IQueryable<AuditLog> ApplyFilters(this IQueryable<AuditLog> rows, AuditLogFilters f)
    {
        if (f.From is { } from)
        {
            rows = rows.Where(a => a.OccurredAtUtc >= from);
        }

        if (f.To is { } to)
        {
            rows = rows.Where(a => a.OccurredAtUtc <= to);
        }

        if (!string.IsNullOrWhiteSpace(f.Action))
        {
            rows = rows.Where(a => a.Action == f.Action);
        }

        if (!string.IsNullOrWhiteSpace(f.Category))
        {
            // Category = the action prefix, e.g. "wg" matches wg.network.created (and exactly "wg").
            var prefix = f.Category + ".";
            rows = rows.Where(a => a.Action == f.Category || a.Action.StartsWith(prefix));
        }

        if (!string.IsNullOrWhiteSpace(f.Actor))
        {
            var term = f.Actor.ToLower();
            rows = rows.Where(a => a.ActorEmail != null && a.ActorEmail.ToLower().Contains(term));
        }

        if (!string.IsNullOrWhiteSpace(f.Target))
        {
            var term = f.Target.ToLower();
            rows = rows.Where(a =>
                (a.TargetType != null && a.TargetType.ToLower().Contains(term)) ||
                (a.TargetId != null && a.TargetId.ToLower().Contains(term)));
        }

        if (f.Outcome is { } outcome)
        {
            rows = rows.Where(a => a.Outcome == outcome);
        }

        if (!string.IsNullOrWhiteSpace(f.Query))
        {
            var term = f.Query.ToLower();
            rows = rows.Where(a =>
                a.Action.ToLower().Contains(term) ||
                (a.ActorEmail != null && a.ActorEmail.ToLower().Contains(term)) ||
                (a.TargetType != null && a.TargetType.ToLower().Contains(term)) ||
                (a.TargetId != null && a.TargetId.ToLower().Contains(term)));
        }

        return rows;
    }

    /// <summary>
    /// The keyset predicate: rows strictly after <paramref name="cursor"/> in <c>(occurred_at_utc, id) DESC</c>
    /// order. Use together with an <c>OrderByDescending(occurred_at_utc).ThenByDescending(id)</c> — both the
    /// <c>id</c> tie-break here (<c>Guid.CompareTo</c> → Npgsql <c>uuid</c> comparison) and that ORDER BY resolve
    /// on the database in the SAME (Postgres uuid) ordering, so page boundaries are exact even when two rows
    /// share a microsecond timestamp. (Verified against a real DB — runtime page-boundary test.)
    /// </summary>
    public static IQueryable<AuditLog> ApplyKeyset(this IQueryable<AuditLog> rows, AuditCursor? cursor)
    {
        if (cursor is { } c)
        {
            var t = c.OccurredAtUtc;
            var id = c.Id;
            rows = rows.Where(a => a.OccurredAtUtc < t || (a.OccurredAtUtc == t && a.Id.CompareTo(id) < 0));
        }

        return rows;
    }

    /// <summary>
    /// Trim an over-fetched list (page size + 1) to the page and derive the next cursor from the last kept
    /// row's key. A null next cursor means the final page was reached.
    /// </summary>
    public static CursorPage<T> ToCursorPage<T>(this List<T> overFetched, int pageSize, Func<T, AuditCursor> keyOf)
    {
        string? next = null;
        if (overFetched.Count > pageSize)
        {
            overFetched.RemoveRange(pageSize, overFetched.Count - pageSize);
            next = keyOf(overFetched[^1]).Encode();
        }

        return new CursorPage<T>(overFetched, next);
    }
}

/// <summary>The last-seen audit row's keyset position, serialised into an opaque base64url cursor.</summary>
public readonly record struct AuditCursor(DateTimeOffset OccurredAtUtc, Guid Id)
{
    public string Encode()
    {
        var bytes = Encoding.UTF8.GetBytes($"{OccurredAtUtc.UtcTicks}:{Id}");
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>Decode a cursor; returns null for a missing or malformed value (caller treats null as the first page).</summary>
    public static AuditCursor? TryDecode(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return null;
        }

        try
        {
            var s = cursor.Replace('-', '+').Replace('_', '/');
            s += (s.Length % 4) switch { 2 => "==", 3 => "=", _ => "" };
            var raw = Encoding.UTF8.GetString(Convert.FromBase64String(s));
            var sep = raw.IndexOf(':');
            if (sep <= 0 ||
                !long.TryParse(raw[..sep], out var ticks) ||
                !Guid.TryParse(raw[(sep + 1)..], out var id))
            {
                return null;
            }

            return new AuditCursor(new DateTimeOffset(ticks, TimeSpan.Zero), id);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}

/// <summary>
/// The rich audit filter set, shared by the tenant and platform reads. Built from raw request strings via
/// <see cref="Create"/> (which parses the outcome). All fields are optional — an empty filter matches everything.
/// </summary>
public sealed record AuditLogFilters(
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    string? Action = null,
    string? Category = null,
    string? Actor = null,
    string? Target = null,
    AuditOutcome? Outcome = null,
    string? Query = null)
{
    public static AuditLogFilters Create(
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        string? action = null,
        string? category = null,
        string? actor = null,
        string? target = null,
        string? outcome = null,
        string? query = null)
    {
        AuditOutcome? parsedOutcome =
            Enum.TryParse<AuditOutcome>(outcome, ignoreCase: true, out var o) ? o : null;

        return new AuditLogFilters(from, to, action, category, actor, target, parsedOutcome, query);
    }
}
