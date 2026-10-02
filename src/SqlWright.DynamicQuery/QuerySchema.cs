using SqlWright.Mapping;

namespace SqlWright.DynamicQuery;

/// <summary>
/// The allowlist of tables and columns API clients may query, plus limits. Build one at startup and reuse it;
/// it is safe to share across requests once configured.
/// </summary>
/// <remarks>
/// Client requests only ever refer to names declared here. Generated SQL contains identifiers from this schema and
/// parameters for every value, never text from the request, so requests cannot inject SQL or reach undeclared data.
/// </remarks>
/// <example>
/// <code>
/// var schema = new QuerySchema()
///     .Table&lt;Customer&gt;("customers", t =&gt; t.Ignore(nameof(Customer.PasswordHash)))
///     .Table("orders", t =&gt; t
///         .From("Orders", schema: "sales")
///         .Key("id")
///         .Column&lt;int&gt;("id", "Id")
///         .Column&lt;int&gt;("customerId", "CustomerId")
///         .Column&lt;decimal&gt;("total", "Total")
///         .Column&lt;DateTime&gt;("placedAt", "PlacedAt"));
/// </code>
/// </example>
public sealed class QuerySchema
{
    private readonly Dictionary<string, TableDefinition> _tables = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Limits that protect the database from expensive requests.</summary>
    public QueryLimits Limits { get; } = new();

    /// <summary>The declared tables.</summary>
    public IReadOnlyCollection<TableDefinition> Tables => _tables.Values;

    /// <summary>Declares a table by hand.</summary>
    /// <param name="name">The name clients use. Also the database table name unless <see cref="TableBuilder.From"/> says otherwise.</param>
    /// <param name="configure">Declares columns, keys and the database table.</param>
    public QuerySchema Table(string name, Action<TableBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new TableBuilder(name);
        configure(builder);
        return Add(builder.Build());
    }

    /// <summary>
    /// Declares a table from an entity type, using SqlWright's mapping (<c>[Table]</c>, <c>[Column]</c>, <c>[Key]</c>,
    /// <see cref="SqlWrightSettings.NamingStyle"/>). Every mapped member is exposed, under its camelCase name,
    /// unless removed with <see cref="TableBuilder.Ignore"/>.
    /// </summary>
    /// <param name="name">The name clients use. Defaults to the camelCase type name.</param>
    /// <param name="configure">Optional changes: ignore sensitive members, add columns, etc.</param>
    public QuerySchema Table<TEntity>(string? name = null, Action<TableBuilder>? configure = null)
    {
        var metadata = EntityMetadata.For<TEntity>();
        var builder = new TableBuilder(name ?? CamelCase(typeof(TEntity).Name)).From(metadata.TableName, metadata.Schema);
        foreach (var column in metadata.Columns)
            builder.Column(CamelCase(column.MemberName), column.ColumnName, column.Type);
        if (metadata.Keys.Count > 0)
            builder.Key(metadata.Keys.Select(k => CamelCase(k.MemberName)).ToArray());

        configure?.Invoke(builder);
        return Add(builder.Build());
    }

    internal TableDefinition? Find(string name) => _tables.GetValueOrDefault(name);

    private QuerySchema Add(TableDefinition table)
    {
        if (!_tables.TryAdd(table.Name, table))
            throw new ArgumentException($"Table '{table.Name}' is already declared.", nameof(table));
        return this;
    }

    internal static string CamelCase(string name) =>
        name.Length == 0 || char.IsLower(name[0]) ? name : char.ToLowerInvariant(name[0]) + name[1..];
}

/// <summary>Limits on what a single request may ask for. Violations are reported as validation errors.</summary>
public sealed class QueryLimits
{
    /// <summary>Largest allowed page size. Default 100.</summary>
    public int MaxPageSize { get; set; } = 100;

    /// <summary>Page size when the request has no pagination. Default 20.</summary>
    public int DefaultPageSize { get; set; } = 20;

    /// <summary>Largest allowed row offset in offset mode (deep offsets are slow). Default 10,000; use cursor mode beyond.</summary>
    public int MaxOffset { get; set; } = 10_000;

    /// <summary>Most joins per request. Default 3.</summary>
    public int MaxJoins { get; set; } = 3;

    /// <summary>Most conditions (across WHERE and HAVING) per request. Default 50.</summary>
    public int MaxConditions { get; set; } = 50;

    /// <summary>Deepest nesting of condition groups. Default 5.</summary>
    public int MaxNestingDepth { get; set; } = 5;

    /// <summary>Most values in an <c>in</c> / <c>notIn</c> list. Default 500.</summary>
    public int MaxInValues { get; set; } = 500;

    /// <summary>Most selected fields. Default 50.</summary>
    public int MaxSelectFields { get; set; } = 50;

    /// <summary>Command timeout in seconds for dynamic queries. <c>null</c> uses SqlWright's default.</summary>
    public int? CommandTimeout { get; set; }
}

/// <summary>Configures one table of a <see cref="QuerySchema"/>.</summary>
public sealed class TableBuilder
{
    private readonly string _name;
    private string _table;
    private string? _dbSchema;
    private readonly List<ColumnDefinition> _columns = [];
    private readonly List<string> _keys = [];

    internal TableBuilder(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Table name is required.", nameof(name));
        _name = name;
        _table = name;
    }

    /// <summary>Sets the database table (and schema) when it differs from the exposed name.</summary>
    public TableBuilder From(string table, string? schema = null)
    {
        _table = table;
        _dbSchema = schema;
        return this;
    }

    /// <summary>Exposes a column.</summary>
    /// <param name="name">The name clients use.</param>
    /// <param name="column">The database column. Defaults to <paramref name="name"/>.</param>
    /// <param name="type">
    /// The column's .NET type. Client values are converted to it (so <c>"2026-01-01"</c> becomes a DateTime and
    /// bad values are rejected with a clear message). Strongly recommended; <c>null</c> passes JSON values through as-is.
    /// </param>
    public TableBuilder Column(string name, string? column = null, Type? type = null)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Column name is required.", nameof(name));
        _columns.RemoveAll(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
        _columns.Add(new ColumnDefinition(name, column ?? name, type));
        return this;
    }

    /// <summary>Exposes a column of type <typeparamref name="T"/>.</summary>
    public TableBuilder Column<T>(string name, string? column = null) => Column(name, column, typeof(T));

    /// <summary>Exposes several columns whose database names match the exposed names. Prefer typed <see cref="Column{T}"/>.</summary>
    public TableBuilder Columns(params string[] names)
    {
        foreach (var name in names) Column(name);
        return this;
    }

    /// <summary>Removes a column, e.g. a sensitive member added by <see cref="QuerySchema.Table{TEntity}"/>. Matches exposed or member name.</summary>
    public TableBuilder Ignore(string name)
    {
        var camel = QuerySchema.CamelCase(name);
        _columns.RemoveAll(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)
                                || string.Equals(c.Name, camel, StringComparison.OrdinalIgnoreCase));
        _keys.RemoveAll(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase)
                             || string.Equals(k, camel, StringComparison.OrdinalIgnoreCase));
        return this;
    }

    /// <summary>
    /// Declares the unique key (exposed column names). Cursor pagination uses it to break ties so every row is
    /// returned exactly once.
    /// </summary>
    public TableBuilder Key(params string[] columns)
    {
        _keys.Clear();
        _keys.AddRange(columns);
        return this;
    }

    internal TableDefinition Build()
    {
        if (_columns.Count == 0)
            throw new InvalidOperationException($"Table '{_name}' must expose at least one column.");

        var keys = new List<ColumnDefinition>();
        foreach (var key in _keys)
        {
            keys.Add(_columns.FirstOrDefault(c => string.Equals(c.Name, key, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Key '{key}' of table '{_name}' is not an exposed column."));
        }
        return new TableDefinition(_name, _table, _dbSchema, _columns.ToArray(), keys.ToArray());
    }
}

/// <summary>An exposed table.</summary>
public sealed class TableDefinition
{
    internal TableDefinition(string name, string table, string? schema, ColumnDefinition[] columns, ColumnDefinition[] keys)
    {
        Name = name;
        DbTable = table;
        DbSchema = schema;
        Columns = columns;
        Keys = keys;
    }

    /// <summary>The name clients use.</summary>
    public string Name { get; }

    /// <summary>The database table.</summary>
    public string DbTable { get; }

    /// <summary>The database schema, if any.</summary>
    public string? DbSchema { get; }

    /// <summary>Exposed columns.</summary>
    public IReadOnlyList<ColumnDefinition> Columns { get; }

    /// <summary>Key columns, for cursor pagination.</summary>
    public IReadOnlyList<ColumnDefinition> Keys { get; }

    internal ColumnDefinition? Find(string name) =>
        Columns.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>An exposed column.</summary>
public sealed class ColumnDefinition
{
    internal ColumnDefinition(string name, string dbColumn, Type? type)
    {
        Name = name;
        DbColumn = dbColumn;
        Type = type;
    }

    /// <summary>The name clients use.</summary>
    public string Name { get; }

    /// <summary>The database column.</summary>
    public string DbColumn { get; }

    /// <summary>The .NET type client values are converted to, if declared.</summary>
    public Type? Type { get; }
}
