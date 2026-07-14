namespace WireHQ.Shared.Primitives;

/// <summary>
/// Normalized paging input. Clamps to safe bounds so a caller can never request an unbounded
/// or non-positive page — a small but important guard against accidental table scans.
/// </summary>
public readonly record struct PaginationRequest
{
    public const int MaxPageSize = 100;
    public const int DefaultPageSize = 25;

    public PaginationRequest(int page = 1, int pageSize = DefaultPageSize)
    {
        Page = page < 1 ? 1 : page;
        PageSize = pageSize switch
        {
            < 1 => DefaultPageSize,
            > MaxPageSize => MaxPageSize,
            _ => pageSize,
        };
    }

    public int Page { get; }

    public int PageSize { get; }

    public int Skip => (Page - 1) * PageSize;
}
