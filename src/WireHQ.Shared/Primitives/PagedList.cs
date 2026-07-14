namespace WireHQ.Shared.Primitives;

/// <summary>
/// The canonical paged collection returned by list queries. Mirrors the API collection
/// envelope (see docs/06-api-design.md) so the contract is identical end to end.
/// </summary>
public sealed class PagedList<T>
{
    public PagedList(IReadOnlyList<T> items, int page, int pageSize, int total)
    {
        Items = items;
        Page = page;
        PageSize = pageSize;
        Total = total;
    }

    public IReadOnlyList<T> Items { get; }

    public int Page { get; }

    public int PageSize { get; }

    public int Total { get; }

    public int TotalPages => PageSize == 0 ? 0 : (int)Math.Ceiling(Total / (double)PageSize);

    public bool HasNextPage => Page < TotalPages;

    public bool HasPreviousPage => Page > 1;

    public static PagedList<T> Empty(int pageSize) => new([], 1, pageSize, 0);
}
