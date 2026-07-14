namespace WireHQ.Shared.Primitives;

/// <summary>
/// A keyset-paginated slice of a list. Unlike <see cref="PagedList{T}"/> (offset paging with a total
/// count) this carries an opaque <see cref="NextCursor"/> — pass it back to fetch the following page.
/// Keyset paging is stable under inserts and cheap at any depth (no <c>OFFSET</c> table scan, no
/// <c>COUNT(*)</c>), which is what an append-only, ever-growing audit log needs. <see cref="NextCursor"/>
/// is null when the last page has been reached. (docs/15 §5)
/// </summary>
public sealed record CursorPage<T>(IReadOnlyList<T> Items, string? NextCursor)
{
    public static CursorPage<T> Empty => new([], null);
}
