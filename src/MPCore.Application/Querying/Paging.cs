namespace MPCore.Application.Querying;

/// <summary>
/// One page of a query, expressed as values a caller cannot get wrong: the number is at least one and
/// the size is clamped to <see cref="MaximumSize"/>, so a request for a million rows is a large page
/// rather than an outage.
/// </summary>
public sealed record PageRequest
{
    /// <summary>The size used when a caller does not choose one.</summary>
    public const int DefaultSize = 20;

    /// <summary>The largest page any query serves.</summary>
    public const int MaximumSize = 200;

    /// <summary>Creates a page request, normalising both values.</summary>
    /// <param name="number">One-based page number; anything lower is read as the first page.</param>
    /// <param name="size">Rows per page; clamped between one and <see cref="MaximumSize"/>.</param>
    public PageRequest(int number = 1, int size = DefaultSize)
    {
        Number = number < 1 ? 1 : number;
        Size = Math.Clamp(size, 1, MaximumSize);
    }

    /// <summary>The one-based page number.</summary>
    public int Number { get; }

    /// <summary>The number of rows on the page.</summary>
    public int Size { get; }

    /// <summary>Rows to skip to reach this page.</summary>
    public int Skip => (Number - 1) * Size;

    /// <summary>The first page at the default size.</summary>
    public static PageRequest First { get; } = new();
}

/// <summary>Sort order.</summary>
public enum SortDirection
{
    /// <summary>Smallest first.</summary>
    Ascending = 0,

    /// <summary>Largest first.</summary>
    Descending = 1,
}

/// <summary>
/// A requested sort. The field is a name the query publishes, never an expression: an adapter resolves
/// it through a <see cref="SortAllowlist"/> before it can reach a database.
/// </summary>
/// <param name="Field">The field name, as published by the query contract.</param>
/// <param name="Direction">The order.</param>
public sealed record SortSpec(string Field, SortDirection Direction = SortDirection.Ascending)
{
    /// <summary>The same sort in the other direction.</summary>
    public SortSpec Reversed() => this with { Direction = Direction == SortDirection.Ascending ? SortDirection.Descending : SortDirection.Ascending };
}

/// <summary>One page of results and the total the query matched.</summary>
/// <typeparam name="TItem">The projected item type, a read model rather than an entity.</typeparam>
/// <param name="Items">The rows on this page, in the requested order.</param>
/// <param name="Number">The one-based page number these rows came from.</param>
/// <param name="Size">The page size that was applied.</param>
/// <param name="Total">How many rows matched in total, ignoring paging.</param>
public sealed record Page<TItem>(IReadOnlyList<TItem> Items, int Number, int Size, long Total)
{
    /// <summary>An empty page shaped for the request that found nothing.</summary>
    /// <param name="request">The request that was served.</param>
    public static Page<TItem> Empty(PageRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new Page<TItem>([], request.Number, request.Size, 0);
    }

    /// <summary>How many pages the total spans at this size.</summary>
    public int PageCount => Size <= 0 ? 0 : (int)Math.Ceiling(Total / (double)Size);

    /// <summary>Whether another page exists after this one.</summary>
    public bool HasMore => (long)Number * Size < Total;
}
