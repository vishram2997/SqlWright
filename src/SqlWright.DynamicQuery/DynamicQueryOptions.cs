namespace SqlWright.DynamicQuery;

/// <summary>
/// Per-request server-side settings, chiefly row filters such as tenant or ownership checks that clients can't bypass.
/// </summary>
/// <example>
/// <code>
/// var options = new DynamicQueryOptions()
///     .Filter("orders", o =&gt; $"{o["TenantId"]} = {tenantId}")
///     .Filter("customers", c =&gt; $"{c["TenantId"]} = {tenantId}");
/// var page = await connection.QueryDynamicAsync(request, schema, options);
/// </code>
/// </example>
public sealed class DynamicQueryOptions
{
    private readonly Dictionary<string, Func<TableReference, Sql>> _filters = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Adds a condition applied every time <paramref name="table"/> appears in a query: in WHERE for the main
    /// table, in the join's ON clause for joined tables (so outer joins keep their meaning). The callback gets a
    /// reference to the table's alias in the generated SQL; index it with database column names.
    /// </summary>
    public DynamicQueryOptions Filter(string table, Func<TableReference, Sql> filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        _filters[table] = filter;
        return this;
    }

    internal Func<TableReference, Sql>? FilterFor(string table) => _filters.GetValueOrDefault(table);
}

/// <summary>A table as it appears in generated SQL, for writing row filters.</summary>
public sealed class TableReference
{
    private readonly SqlDialect _dialect;

    internal TableReference(SqlDialect dialect, string alias)
    {
        _dialect = dialect;
        Alias = alias;
    }

    /// <summary>The table's alias in the generated SQL.</summary>
    public string Alias { get; }

    /// <summary>A quoted, alias-qualified reference to a database column, e.g. <c>[t0].[TenantId]</c>. Columns here needn't be exposed.</summary>
    public Sql this[string column] => Sql.Raw(_dialect.QuoteIdentifier(Alias) + "." + _dialect.QuoteIdentifier(column));
}
