using SqlWright.DynamicQuery.Internal;

namespace SqlWright.DynamicQuery;

/// <summary>
/// The SQL generated for a <see cref="QueryRequest"/>. Use <see cref="Sql.Text"/> and <see cref="Sql.Values"/>
/// for logging or debugging; <c>QueryDynamic</c> runs it for you.
/// </summary>
public sealed class TranslatedQuery
{
    private readonly string? _fingerprint;
    private readonly IReadOnlyList<string> _hiddenColumns;

    internal TranslatedQuery(Sql data, Sql? count, int pageSize, int? page, string? fingerprint, IReadOnlyList<string> hiddenColumns)
    {
        Data = data;
        Count = count;
        PageSize = pageSize;
        Page = page;
        _fingerprint = fingerprint;
        _hiddenColumns = hiddenColumns;
    }

    /// <summary>The query for the page of rows. It fetches one extra row to detect whether another page exists.</summary>
    public Sql Data { get; }

    /// <summary>The total-count query, when the request asked for it.</summary>
    public Sql? Count { get; }

    /// <summary>The page size.</summary>
    public int PageSize { get; }

    /// <summary>The page number, in offset mode.</summary>
    public int? Page { get; }

    internal QueryResponse ToResponse(IEnumerable<object> rows, long? totalCount)
    {
        var list = rows.Cast<IDictionary<string, object?>>().ToList();
        var hasNextPage = list.Count > PageSize;
        if (hasNextPage) list.RemoveAt(list.Count - 1);

        string? nextCursor = null;
        if (_fingerprint != null && hasNextPage)
        {
            var last = list[^1];
            nextCursor = Cursor.Encode(_fingerprint, _hiddenColumns.Select(c => last[c]).ToArray());
        }

        var data = list
            .Select(row => row.Where(kv => !_hiddenColumns.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value))
            .ToList();

        return new QueryResponse
        {
            Data = data,
            Page = new PageInfo
            {
                PageSize = PageSize,
                Page = Page,
                TotalCount = totalCount,
                TotalPages = totalCount is { } total && Page != null ? (int)((total + PageSize - 1) / PageSize) : null,
                NextCursor = nextCursor,
                HasNextPage = hasNextPage,
            },
        };
    }
}
