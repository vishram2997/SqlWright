using System.Text.Json;
using System.Text.Json.Serialization;

namespace SqlWright.DynamicQuery;

// ---------- Request ----------

/// <summary>A query sent by an API client. Every table and field it names must be allowed by the server's <see cref="QuerySchema"/>.</summary>
public sealed class QueryRequest
{
    /// <summary>The table to query, by its name in the schema.</summary>
    public required string Table { get; init; }

    /// <summary>An alias the rest of the request can use to qualify fields, e.g. <c>"o"</c> for <c>"o.total"</c>.</summary>
    public string? Alias { get; init; }

    /// <summary>Return only distinct rows.</summary>
    public bool Distinct { get; init; }

    /// <summary>Fields to return. Empty means every field of <see cref="Table"/> in the schema.</summary>
    public List<SelectField> Select { get; init; } = [];

    /// <summary>Tables to join.</summary>
    public List<Join> Joins { get; init; } = [];

    /// <summary>Row filter.</summary>
    public ConditionGroup? Where { get; init; }

    /// <summary>Fields to group by.</summary>
    public List<string> GroupBy { get; init; } = [];

    /// <summary>Filter applied after grouping; conditions may use aggregates.</summary>
    public ConditionGroup? Having { get; init; }

    /// <summary>Sort order. A sort field may also name a select alias.</summary>
    public List<Sort> OrderBy { get; init; } = [];

    /// <summary>Paging. Defaults to the first page of the schema's default page size.</summary>
    public Pagination? Pagination { get; init; }

    /// <summary>Also count all matching rows (one extra query).</summary>
    public bool IncludeTotalCount { get; init; }
}

/// <summary>A field (or aggregate) to return.</summary>
public sealed class SelectField
{
    /// <summary>The field, optionally qualified (<c>"o.total"</c>). <c>"*"</c> is allowed with <see cref="Aggregate.Count"/>.</summary>
    public required string Field { get; init; }

    /// <summary>An aggregate to apply.</summary>
    public Aggregate? Aggregate { get; init; }

    /// <summary>The key for this value in each result row.</summary>
    public string? Alias { get; init; }
}

/// <summary>A join to another table.</summary>
public sealed class Join
{
    /// <summary>The kind of join.</summary>
    public JoinType Type { get; init; } = JoinType.Inner;

    /// <summary>The table to join, by its name in the schema.</summary>
    public required string Table { get; init; }

    /// <summary>An alias for qualifying this table's fields. Required when joining the same table twice.</summary>
    public string? Alias { get; init; }

    /// <summary>Join conditions, combined with AND.</summary>
    public required List<JoinOn> On { get; init; }
}

/// <summary>One join condition comparing two fields.</summary>
public sealed class JoinOn
{
    /// <summary>A field.</summary>
    public required string Left { get; init; }

    /// <summary>A comparison operator (eq, neq, gt, gte, lt, lte).</summary>
    public Operator Operator { get; init; } = Operator.Eq;

    /// <summary>A field.</summary>
    public required string Right { get; init; }
}

/// <summary>A single condition or a nested group. Polymorphic by shape (see <see cref="ConditionNodeConverter"/>).</summary>
[JsonConverter(typeof(ConditionNodeConverter))]
public abstract class ConditionNode;

/// <summary>A comparison of a field (or an aggregate, in HAVING) with a value.</summary>
public sealed class Condition : ConditionNode
{
    /// <summary>The field, optionally qualified.</summary>
    public required string Field { get; init; }

    /// <summary>An aggregate of the field. Only valid inside <see cref="QueryRequest.Having"/>.</summary>
    public Aggregate? Aggregate { get; init; }

    /// <summary>The operator.</summary>
    public required Operator Operator { get; init; }

    /// <summary>A scalar, or an array for <c>in</c>, <c>notIn</c> (any length) and <c>between</c> (two values).</summary>
    public JsonElement? Value { get; init; }

    /// <summary>Compare text case-insensitively.</summary>
    public bool CaseInsensitive { get; init; }
}

/// <summary>Conditions combined with AND or OR, optionally negated.</summary>
public sealed class ConditionGroup : ConditionNode
{
    /// <summary>How the conditions are combined.</summary>
    public Logic Logic { get; init; } = Logic.And;

    /// <summary>Negate the whole group.</summary>
    public bool Not { get; init; }

    /// <summary>The conditions and nested groups.</summary>
    public required List<ConditionNode> Conditions { get; init; }
}

/// <summary>A sort key.</summary>
public sealed class Sort
{
    /// <summary>A field, or the alias of a selected value.</summary>
    public required string Field { get; init; }

    /// <summary>The direction.</summary>
    public SortDirection Direction { get; init; } = SortDirection.Asc;

    /// <summary>Where nulls go. Unset uses the database default (offset mode) or treats nulls as largest (cursor mode).</summary>
    public NullsOrder? Nulls { get; init; }
}

/// <summary>Paging options.</summary>
public sealed class Pagination
{
    /// <summary>Page numbers (offset) or opaque continuation tokens (cursor).</summary>
    public PaginationMode Mode { get; init; } = PaginationMode.Offset;

    /// <summary>The 1-based page number, in offset mode.</summary>
    public int Page { get; init; } = 1;

    /// <summary>Rows per page.</summary>
    public int PageSize { get; init; } = 20;

    /// <summary>In cursor mode, the <see cref="PageInfo.NextCursor"/> from the previous page. Omit for the first page.</summary>
    public string? After { get; init; }
}

// ---------- Response ----------

/// <summary>A page of results.</summary>
public sealed class QueryResponse
{
    /// <summary>The rows, keyed by field name or alias.</summary>
    public required List<Dictionary<string, object?>> Data { get; init; }

    /// <summary>Paging information.</summary>
    public required PageInfo Page { get; init; }
}

/// <summary>Paging information for a <see cref="QueryResponse"/>.</summary>
public sealed class PageInfo
{
    /// <summary>The page size used.</summary>
    public int PageSize { get; init; }

    /// <summary>The page number, in offset mode.</summary>
    public int? Page { get; init; }

    /// <summary>Total matching rows, when requested.</summary>
    public long? TotalCount { get; init; }

    /// <summary>Total pages, when the total count was requested in offset mode.</summary>
    public int? TotalPages { get; init; }

    /// <summary>The token for the next page, in cursor mode.</summary>
    public string? NextCursor { get; init; }

    /// <summary>Whether another page follows.</summary>
    public bool HasNextPage { get; init; }
}

// ---------- Enums (serialized as camelCase strings) ----------

/// <summary>Aggregate functions.</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<Aggregate>))]
public enum Aggregate
{
    /// <summary>COUNT</summary>
    Count,
    /// <summary>COUNT(DISTINCT ...)</summary>
    CountDistinct,
    /// <summary>SUM</summary>
    Sum,
    /// <summary>AVG</summary>
    Avg,
    /// <summary>MIN</summary>
    Min,
    /// <summary>MAX</summary>
    Max,
}

/// <summary>Join kinds.</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<JoinType>))]
public enum JoinType
{
    /// <summary>INNER JOIN</summary>
    Inner,
    /// <summary>LEFT JOIN</summary>
    Left,
    /// <summary>RIGHT JOIN</summary>
    Right,
    /// <summary>FULL OUTER JOIN (not supported by MySQL)</summary>
    Full,
}

/// <summary>Condition operators.</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<Operator>))]
public enum Operator
{
    /// <summary>Equal.</summary>
    Eq,
    /// <summary>Not equal.</summary>
    Neq,
    /// <summary>Greater than.</summary>
    Gt,
    /// <summary>Greater than or equal.</summary>
    Gte,
    /// <summary>Less than.</summary>
    Lt,
    /// <summary>Less than or equal.</summary>
    Lte,
    /// <summary>In a list of values.</summary>
    In,
    /// <summary>Not in a list of values.</summary>
    NotIn,
    /// <summary>Between two values, inclusive.</summary>
    Between,
    /// <summary>Text contains the value.</summary>
    Contains,
    /// <summary>Text does not contain the value.</summary>
    NotContains,
    /// <summary>Text starts with the value.</summary>
    StartsWith,
    /// <summary>Text ends with the value.</summary>
    EndsWith,
    /// <summary>Is null (no value).</summary>
    IsNull,
    /// <summary>Is not null (no value).</summary>
    IsNotNull,
}

/// <summary>How conditions in a group combine.</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<Logic>))]
public enum Logic
{
    /// <summary>All must hold.</summary>
    And,
    /// <summary>Any must hold.</summary>
    Or,
}

/// <summary>Sort direction.</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<SortDirection>))]
public enum SortDirection
{
    /// <summary>Ascending.</summary>
    Asc,
    /// <summary>Descending.</summary>
    Desc,
}

/// <summary>Placement of nulls in a sort.</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<NullsOrder>))]
public enum NullsOrder
{
    /// <summary>Nulls before other values.</summary>
    First,
    /// <summary>Nulls after other values.</summary>
    Last,
}

/// <summary>Paging styles.</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<PaginationMode>))]
public enum PaginationMode
{
    /// <summary>Page numbers. Simple, supports total pages; slower deep into large tables.</summary>
    Offset,
    /// <summary>Continuation tokens (keyset paging). Stable and fast at any depth.</summary>
    Cursor,
}

// ---------- JSON plumbing ----------

/// <summary>Reads and writes enums as camelCase strings (reading is case-insensitive and also accepts numbers).</summary>
public sealed class CamelCaseEnumConverter<TEnum> : JsonStringEnumConverter<TEnum> where TEnum : struct, Enum
{
    /// <summary>Creates the converter.</summary>
    public CamelCaseEnumConverter() : base(JsonNamingPolicy.CamelCase)
    {
    }
}

/// <summary>Reads a <see cref="ConditionNode"/>: an object with <c>conditions</c> is a group, anything else a condition.</summary>
public sealed class ConditionNodeConverter : JsonConverter<ConditionNode>
{
    /// <inheritdoc />
    public override ConditionNode? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;

        var isGroup = root.ValueKind == JsonValueKind.Object &&
                      root.EnumerateObject().Any(p => string.Equals(p.Name, "conditions", StringComparison.OrdinalIgnoreCase));

        return isGroup
            ? root.Deserialize<ConditionGroup>(options)
            : root.Deserialize<Condition>(options);
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, ConditionNode value, JsonSerializerOptions options)
        => JsonSerializer.Serialize(writer, value, value.GetType(), options);
}

/// <summary>Ready-made serializer options for <see cref="QueryRequest"/> and <see cref="QueryResponse"/> (web defaults).</summary>
public static class DynamicQueryJson
{
    /// <summary>camelCase, case-insensitive web defaults.</summary>
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web);
}
