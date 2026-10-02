using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace SqlWright.DynamicQuery.Internal;

/// <summary>
/// Turns a <see cref="QueryRequest"/> into SQL. Every identifier in the output comes from the <see cref="QuerySchema"/>
/// (quoted by the dialect); every client value becomes a parameter. Problems are collected with their JSON path
/// and thrown together as a <see cref="DynamicQueryException"/>.
/// </summary>
internal sealed partial class QueryTranslator
{
    private sealed record TableScope(string Qualifier, TableDefinition Table, string Alias);

    private sealed record FieldRef(TableScope Scope, ColumnDefinition Column, string Expr);

    private sealed record SelectItem(string Key, string Expr, bool IsAggregate, FieldRef? Field);

    private sealed class SortItem(string expr, bool descending, NullsOrder? nulls)
    {
        public string Expr { get; } = expr;
        public bool Descending { get; } = descending;
        public NullsOrder? Nulls { get; set; } = nulls;
    }

    private static readonly JsonSerializerOptions ValueOptions = new(JsonSerializerDefaults.Web)
    {
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly QuerySchema _schema;
    private readonly SqlDialect _dialect;
    private readonly DynamicQueryOptions? _options;
    private readonly List<QueryError> _errors = [];
    private readonly List<TableScope> _scopes = [];
    private readonly List<FieldRef> _groupBy = [];
    private int _conditionCount;

    private QueryTranslator(QuerySchema schema, SqlDialect dialect, DynamicQueryOptions? options)
    {
        _schema = schema;
        _dialect = dialect;
        _options = options;
    }

    private QueryLimits Limits => _schema.Limits;

    public static TranslatedQuery Translate(QuerySchema schema, QueryRequest request, SqlDialect dialect, DynamicQueryOptions? options)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new QueryTranslator(schema, dialect, options).Run(request);
    }

    private TranslatedQuery Run(QueryRequest r)
    {
        // FROM and JOINs
        var baseTable = ResolveTable(r.Table, "table");
        if (baseTable == null) throw new DynamicQueryException(_errors);
        var baseScope = AddScope(baseTable, r.Alias, "alias");
        var from = Sql.Raw($" FROM {TableSql(baseTable)} {Q(baseScope.Alias)}");

        if (r.Joins.Count > Limits.MaxJoins)
            Error("joins", $"At most {Limits.MaxJoins} joins are allowed.");
        for (var i = 0; i < r.Joins.Count && i < Limits.MaxJoins; i++)
        {
            var join = Join(r.Joins[i], $"joins[{i}]");
            if (join != null) from.Append(join);
        }

        // SELECT and GROUP BY
        var select = Select(r, baseScope);
        for (var i = 0; i < r.GroupBy.Count; i++)
        {
            var field = ResolveField(r.GroupBy[i], $"groupBy[{i}]");
            if (field != null) _groupBy.Add(field);
        }

        var grouping = _groupBy.Count > 0 || select.Any(s => s.IsAggregate);
        if (grouping)
        {
            for (var i = 0; i < select.Count; i++)
            {
                var item = select[i];
                if (!item.IsAggregate && item.Field != null && !IsGrouped(item.Field))
                    Error(r.Select.Count == 0 ? "select" : $"select[{i}].field",
                        $"'{item.Key}' must be in groupBy, or wrapped in an aggregate, when the query groups or aggregates.");
            }
        }

        // WHERE
        var where = new List<Sql>();
        var baseFilter = RowFilter(baseScope);
        if (baseFilter != null) where.Add(baseFilter);
        if (r.Where != null)
        {
            var condition = Group(r.Where, "where", having: false, depth: 1);
            if (condition != null) where.Add(condition);
        }

        // HAVING
        Sql? having = null;
        if (r.Having != null)
        {
            if (!grouping) Error("having", "'having' requires groupBy or an aggregate in select. Use 'where' to filter rows.");
            else having = Group(r.Having, "having", having: true, depth: 1);
        }

        // Pagination
        var pagination = r.Pagination;
        var cursorMode = pagination?.Mode == PaginationMode.Cursor;
        var pageSize = pagination?.PageSize ?? Limits.DefaultPageSize;
        var page = pagination?.Page ?? 1;
        if (pageSize < 1 || pageSize > Limits.MaxPageSize)
            Error("pagination.pageSize", $"Must be between 1 and {Limits.MaxPageSize}.");

        var offset = 0;
        if (!cursorMode)
        {
            if (page < 1) Error("pagination.page", "Must be 1 or more.");
            else if ((long)(page - 1) * pageSize > Limits.MaxOffset)
                Error("pagination.page", $"Offset pagination is limited to the first {Limits.MaxOffset:N0} rows. Use cursor pagination to go further.");
            else offset = (page - 1) * pageSize;

            if (pagination?.After != null) Error("pagination.after", "'after' is only used with cursor pagination.");
        }

        // ORDER BY. Without a client sort, order by the key when possible so pages are stable (no repeats or gaps).
        var sorts = Sorts(r, select, grouping);
        if (sorts.Count == 0 && !grouping && !r.Distinct)
            sorts.AddRange(baseTable.Keys.Select(k => new SortItem(ColumnExpr(baseScope, k), false, null)));

        // Cursor (keyset) pagination
        Sql? keyset = null;
        string? fingerprint = null;
        var hidden = new List<string>();
        if (cursorMode)
        {
            if (grouping || r.Distinct)
                Error("pagination.mode", "Cursor pagination can't be combined with groupBy, aggregates or distinct. Use offset pagination.");
            else if (baseTable.Keys.Count == 0)
                Error("pagination.mode", $"Cursor pagination needs a key for table '{baseTable.Name}'. Declare one in the schema with .Key(...).");
            else
            {
                // Key columns make the order total, so every row is returned exactly once.
                foreach (var key in baseTable.Keys)
                {
                    var expr = ColumnExpr(baseScope, key);
                    if (!sorts.Any(s => s.Expr == expr)) sorts.Add(new SortItem(expr, false, NullsOrder.Last));
                }
                // Nulls sort as the largest value unless the client chose otherwise; it must be explicit to page past them.
                foreach (var sort in sorts) sort.Nulls ??= sort.Descending ? NullsOrder.First : NullsOrder.Last;

                for (var i = 0; i < sorts.Count; i++) hidden.Add($"__c{i}");
                fingerprint = Cursor.Fingerprint(baseTable.Name + "|" + string.Join("|", sorts.Select(s => $"{s.Expr} {s.Descending} {s.Nulls}")));

                if (pagination!.After != null)
                {
                    if (!Cursor.TryDecode(pagination.After, out var cursorPrint, out var values) || cursorPrint != fingerprint || values.Length != sorts.Count)
                        Error("pagination.after", "This cursor is invalid, or was created for a different query or sort order. Request the first page again without 'after'.");
                    else
                        keyset = Keyset(sorts, values);
                }
            }
        }

        if (_errors.Count > 0) throw new DynamicQueryException(_errors);

        // Assemble
        Sql Core(bool forCount)
        {
            var columns = select.Select(s => $"{s.Expr} AS {Q(s.Key)}");
            // Cursor mode also returns each sort value, under hidden names, to build the next cursor from the last row.
            if (!forCount && hidden.Count > 0) columns = columns.Concat(sorts.Select((s, i) => $"{s.Expr} AS {Q(hidden[i])}"));

            var sql = Sql.Raw("SELECT " + (r.Distinct ? "DISTINCT " : "") + string.Join(", ", columns));
            sql.Append(from);

            var conditions = forCount || keyset == null ? where : [.. where, keyset];
            if (conditions.Count > 0) sql.Append(Sql.Raw(" WHERE ")).Append(Sql.Join(" AND ", conditions));
            if (_groupBy.Count > 0) sql.Append(Sql.Raw(" GROUP BY " + string.Join(", ", _groupBy.Select(g => g.Expr))));
            if (having != null) sql.Append(Sql.Raw(" HAVING ")).Append(having);
            return sql;
        }

        var data = Core(forCount: false);
        if (sorts.Count > 0) data.Append(Sql.Raw(" ORDER BY " + string.Join(", ", sorts.Select(OrderBySql))));
        data.Append(_dialect.Paginate(pageSize + 1, offset, sorts.Count > 0)); // one extra row reveals whether another page exists

        Sql? count = null;
        if (r.IncludeTotalCount)
        {
            count = Sql.Raw("SELECT COUNT(*) FROM (");
            count.Append(Core(forCount: true)).Append(Sql.Raw(") q"));
        }

        return new TranslatedQuery(data, count, pageSize, cursorMode ? null : page, fingerprint, hidden);
    }

    // ---- Tables and joins ----

    private TableDefinition? ResolveTable(string? name, string path)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            Error(path, "A table is required.");
            return null;
        }

        var table = _schema.Find(name);
        if (table == null)
            Error(path, $"Unknown table '{name}'." + Hint(name, _schema.Tables.Select(t => t.Name)));
        return table;
    }

    private TableScope AddScope(TableDefinition table, string? alias, string path)
    {
        if (alias != null && !IsValidName(alias))
            Error(path, "Aliases may contain letters, digits and underscores (up to 64), must not start with a digit or '__'.");

        var qualifier = alias ?? table.Name;
        if (_scopes.Any(s => string.Equals(s.Qualifier, qualifier, StringComparison.OrdinalIgnoreCase)))
            Error(path, $"'{qualifier}' is already used in this query. Give this table a different alias.");

        var scope = new TableScope(qualifier, table, "t" + _scopes.Count);
        _scopes.Add(scope);
        return scope;
    }

    private Sql? Join(Join join, string path)
    {
        var table = ResolveTable(join.Table, path + ".table");
        if (table == null) return null;

        if (join.Type == JoinType.Full && !_dialect.SupportsFullOuterJoin)
            Error(path + ".type", "This database doesn't support full joins.");

        var scope = AddScope(table, join.Alias, path + ".alias");
        var conditions = new List<Sql>();

        if (join.On == null || join.On.Count == 0)
        {
            Error(path + ".on", "At least one join condition is required.");
        }
        else
        {
            for (var i = 0; i < join.On.Count; i++)
            {
                var on = join.On[i];
                var left = ResolveField(on.Left, $"{path}.on[{i}].left");
                var right = ResolveField(on.Right, $"{path}.on[{i}].right");
                var op = ComparisonSql(on.Operator);
                if (op == null) Error($"{path}.on[{i}].operator", "Join conditions support eq, neq, gt, gte, lt and lte.");
                if (left != null && right != null && op != null)
                    conditions.Add(Sql.Raw($"{left.Expr} {op} {right.Expr}"));
            }
        }

        var filter = RowFilter(scope);
        if (filter != null) conditions.Add(filter);
        if (conditions.Count == 0) return null;

        var keyword = join.Type switch
        {
            JoinType.Left => " LEFT JOIN ",
            JoinType.Right => " RIGHT JOIN ",
            JoinType.Full => " FULL OUTER JOIN ",
            _ => " INNER JOIN ",
        };
        var sql = Sql.Raw($"{keyword}{TableSql(table)} {Q(scope.Alias)} ON ");
        return sql.Append(Sql.Join(" AND ", conditions));
    }

    private Sql? RowFilter(TableScope scope)
    {
        var filter = _options?.FilterFor(scope.Table.Name);
        if (filter == null) return null;

        var condition = Sql.Raw("(");
        return condition.Append(filter(new TableReference(_dialect, scope.Alias))).Append(Sql.Raw(")"));
    }

    // ---- Fields ----

    private FieldRef? ResolveField(string? field, string path)
    {
        if (string.IsNullOrWhiteSpace(field))
        {
            Error(path, "A field is required.");
            return null;
        }

        var dot = field.IndexOf('.');
        if (dot > 0)
        {
            var qualifier = field[..dot];
            var name = field[(dot + 1)..];
            var scope = _scopes.FirstOrDefault(s => string.Equals(s.Qualifier, qualifier, StringComparison.OrdinalIgnoreCase));
            if (scope == null)
            {
                Error(path, $"Unknown table or alias '{qualifier}'. Use one of: {string.Join(", ", _scopes.Select(s => s.Qualifier))}.");
                return null;
            }

            var column = scope.Table.Find(name);
            if (column == null)
            {
                Error(path, $"Unknown field '{name}' on '{qualifier}'." + Hint(name, scope.Table.Columns.Select(c => c.Name)));
                return null;
            }
            return Field(scope, column);
        }

        var baseColumn = _scopes[0].Table.Find(field);
        if (baseColumn != null) return Field(_scopes[0], baseColumn);

        var matches = _scopes.Skip(1)
            .Select(s => (Scope: s, Column: s.Table.Find(field)))
            .Where(m => m.Column != null)
            .ToList();
        if (matches.Count == 1) return Field(matches[0].Scope, matches[0].Column!);

        if (matches.Count > 1)
            Error(path, $"Field '{field}' exists on several joined tables ({string.Join(", ", matches.Select(m => m.Scope.Qualifier))}). " +
                        $"Qualify it, e.g. '{matches[0].Scope.Qualifier}.{field}'.");
        else
            Error(path, $"Unknown field '{field}'." + Hint(field, _scopes.SelectMany(s => s.Table.Columns.Select(c => c.Name))));
        return null;
    }

    private FieldRef Field(TableScope scope, ColumnDefinition column) => new(scope, column, ColumnExpr(scope, column));

    private string ColumnExpr(TableScope scope, ColumnDefinition column) => Q(scope.Alias) + "." + Q(column.DbColumn);

    private bool IsGrouped(FieldRef field) => _groupBy.Any(g => g.Expr == field.Expr);

    private List<SelectItem> Select(QueryRequest r, TableScope baseScope)
    {
        var items = new List<SelectItem>();
        if (r.Select.Count == 0)
        {
            foreach (var column in baseScope.Table.Columns)
            {
                var field = Field(baseScope, column);
                items.Add(new SelectItem(column.Name, field.Expr, false, field));
            }
            return items;
        }

        if (r.Select.Count > Limits.MaxSelectFields)
            Error("select", $"At most {Limits.MaxSelectFields} fields may be selected.");

        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < r.Select.Count; i++)
        {
            var s = r.Select[i];
            var path = $"select[{i}]";

            if (s.Alias != null && !IsValidName(s.Alias))
                Error(path + ".alias", "Aliases may contain letters, digits and underscores (up to 64), must not start with a digit or '__'.");

            SelectItem item;
            if (s.Field == "*")
            {
                if (s.Aggregate != Aggregate.Count)
                {
                    Error(path + ".field", "'*' can only be used with the count aggregate.");
                    continue;
                }
                item = new SelectItem(s.Alias ?? "count", "COUNT(*)", true, null);
            }
            else
            {
                var field = ResolveField(s.Field, path + ".field");
                if (field == null) continue;

                item = s.Aggregate is { } aggregate
                    ? new SelectItem(s.Alias ?? $"{QuerySchema.CamelCase(aggregate.ToString())}_{field.Column.Name}", AggregateSql(aggregate, field.Expr), true, null)
                    : new SelectItem(s.Alias ?? s.Field, field.Expr, false, field);
            }

            if (!keys.Add(item.Key))
            {
                Error(path, $"Two selected values are both named '{item.Key}'. Give one an alias.");
                continue;
            }
            items.Add(item);
        }
        return items;
    }

    private List<SortItem> Sorts(QueryRequest r, List<SelectItem> select, bool grouping)
    {
        var sorts = new List<SortItem>();
        for (var i = 0; i < r.OrderBy.Count; i++)
        {
            var s = r.OrderBy[i];
            var path = $"orderBy[{i}].field";

            string expr;
            var selected = select.FirstOrDefault(x => string.Equals(x.Key, s.Field, StringComparison.OrdinalIgnoreCase));
            if (selected != null)
            {
                expr = selected.Expr;
            }
            else
            {
                var field = ResolveField(s.Field, path);
                if (field == null) continue;
                if (grouping && !IsGrouped(field))
                {
                    Error(path, $"'{s.Field}' must be in groupBy (or sort by an aggregate's alias) when the query groups.");
                    continue;
                }
                if (r.Distinct && select.All(x => x.Expr != field.Expr))
                {
                    Error(path, $"With distinct, '{s.Field}' must also be selected to sort by it.");
                    continue;
                }
                expr = field.Expr;
            }
            sorts.Add(new SortItem(expr, s.Direction == SortDirection.Desc, s.Nulls));
        }
        return sorts;
    }

    private static string OrderBySql(SortItem sort)
    {
        var direction = sort.Descending ? " DESC" : " ASC";
        if (sort.Nulls == null) return sort.Expr + direction;

        // Portable NULLS FIRST/LAST: not every database supports the syntax.
        var nullsFirst = sort.Nulls == NullsOrder.First;
        return $"CASE WHEN {sort.Expr} IS NULL THEN {(nullsFirst ? 0 : 1)} ELSE {(nullsFirst ? 1 : 0)} END, {sort.Expr}{direction}";
    }

    /// <summary>
    /// "Rows after the cursor" for a multi-column sort: (a &gt; @a) OR (a = @a AND b &gt; @b) OR ..., with null handling
    /// that matches the explicit NULLS FIRST/LAST order.
    /// </summary>
    private static Sql Keyset(List<SortItem> sorts, object?[] values)
    {
        var alternatives = new List<Sql>();
        for (var i = 0; i < sorts.Count; i++)
        {
            var parts = new List<Sql>();
            for (var j = 0; j < i; j++)
                parts.Add(values[j] is null ? Sql.Raw($"{sorts[j].Expr} IS NULL") : Compare(sorts[j].Expr, "=", values[j]));
            parts.Add(After(sorts[i], values[i]));

            var alternative = Sql.Raw("(");
            alternatives.Add(alternative.Append(Sql.Join(" AND ", parts)).Append(Sql.Raw(")")));
        }

        var keyset = Sql.Raw("(");
        return keyset.Append(Sql.Join(" OR ", alternatives)).Append(Sql.Raw(")"));

        static Sql After(SortItem sort, object? value)
        {
            var nullsFirst = sort.Nulls == NullsOrder.First;
            if (value is null) return Sql.Raw(nullsFirst ? $"{sort.Expr} IS NOT NULL" : "1 = 0");

            var comparison = Compare(sort.Expr, sort.Descending ? "<" : ">", value);
            if (nullsFirst) return comparison;

            var orNull = Sql.Raw("(");
            return orNull.Append(comparison).Append(Sql.Raw($" OR {sort.Expr} IS NULL)"));
        }
    }

    // ---- Conditions ----

    private Sql? Group(ConditionGroup group, string path, bool having, int depth)
    {
        if (depth > Limits.MaxNestingDepth)
        {
            Error(path, $"Condition groups may be nested at most {Limits.MaxNestingDepth} levels deep.");
            return null;
        }
        if (group.Conditions == null)
        {
            Error(path + ".conditions", "A condition group needs a 'conditions' list.");
            return null;
        }

        var parts = new List<Sql>();
        for (var i = 0; i < group.Conditions.Count; i++)
        {
            var childPath = $"{path}.conditions[{i}]";
            var part = group.Conditions[i] switch
            {
                Condition condition => Condition(condition, childPath, having),
                ConditionGroup child => Group(child, childPath, having, depth + 1),
                _ => Invalid(childPath),
            };
            if (part != null) parts.Add(part);
        }
        if (parts.Count == 0) return null;

        var sql = Sql.Raw(group.Not ? "NOT (" : "(");
        return sql.Append(Sql.Join(group.Logic == Logic.Or ? " OR " : " AND ", parts)).Append(Sql.Raw(")"));

        Sql? Invalid(string p)
        {
            Error(p, "Expected a condition or a condition group.");
            return null;
        }
    }

    private Sql? Condition(Condition c, string path, bool having)
    {
        if (++_conditionCount == Limits.MaxConditions + 1)
            Error(path, $"At most {Limits.MaxConditions} conditions are allowed.");
        if (_conditionCount > Limits.MaxConditions) return null;

        string expr;
        Type? type;
        if (c.Aggregate is { } aggregate)
        {
            if (!having)
            {
                Error(path + ".aggregate", "Aggregates are only allowed in 'having' conditions.");
                return null;
            }

            if (c.Field == "*")
            {
                if (aggregate != Aggregate.Count)
                {
                    Error(path + ".field", "'*' can only be used with the count aggregate.");
                    return null;
                }
                expr = "COUNT(*)";
                type = typeof(long);
            }
            else
            {
                var field = ResolveField(c.Field, path + ".field");
                if (field == null) return null;
                expr = AggregateSql(aggregate, field.Expr);
                type = aggregate switch
                {
                    Aggregate.Count or Aggregate.CountDistinct => typeof(long),
                    // AVG is fractional even over integers. SUM and MIN/MAX share the column's type; a decimal would be
                    // wrong for some providers (Microsoft.Data.Sqlite binds decimal as TEXT, which never equals a number).
                    Aggregate.Avg => typeof(double),
                    _ => field.Column.Type,
                };
            }
        }
        else
        {
            var field = ResolveField(c.Field, path + ".field");
            if (field == null) return null;
            if (having && !IsGrouped(field))
            {
                Error(path + ".field", $"'{c.Field}' must be in groupBy to be used in 'having' without an aggregate.");
                return null;
            }
            expr = field.Expr;
            type = field.Column.Type;
        }

        var valuePath = path + ".value";
        switch (c.Operator)
        {
            case Operator.IsNull:
                return Sql.Raw($"{expr} IS NULL");
            case Operator.IsNotNull:
                return Sql.Raw($"{expr} IS NOT NULL");

            case Operator.Eq or Operator.Neq or Operator.Gt or Operator.Gte or Operator.Lt or Operator.Lte:
            {
                if (!TryScalar(c, type, valuePath, out var value)) return null;
                var (lhs, folded) = Fold(expr, value, c.CaseInsensitive);
                return Compare(lhs, ComparisonSql(c.Operator)!, folded);
            }

            case Operator.In or Operator.NotIn:
            {
                if (!TryArray(c, valuePath, out var items)) return null;
                if (items.Count > Limits.MaxInValues)
                {
                    Error(valuePath, $"At most {Limits.MaxInValues} values are allowed.");
                    return null;
                }
                if (items.Count == 0) return Sql.Raw(c.Operator == Operator.In ? "1 = 0" : "1 = 1");

                var values = new object?[items.Count];
                var lhs = expr;
                for (var i = 0; i < items.Count; i++)
                {
                    if (!TryConvert(items[i], type, $"{valuePath}[{i}]", out var value)) return null;
                    if (value is null)
                    {
                        Error($"{valuePath}[{i}]", "Lists can't contain null. Combine with an isNull condition instead.");
                        return null;
                    }
                    (lhs, values[i]) = Fold(expr, value, c.CaseInsensitive);
                }
                var sql = Sql.Raw(lhs + (c.Operator == Operator.In ? " IN " : " NOT IN "));
                sql.AppendFormatted<object>(values);
                return sql;
            }

            case Operator.Between:
            {
                if (!TryArray(c, valuePath, out var items)) return null;
                if (items.Count != 2)
                {
                    Error(valuePath, "'between' needs an array of exactly two values: [low, high].");
                    return null;
                }
                if (!TryConvert(items[0], type, valuePath + "[0]", out var low) | !TryConvert(items[1], type, valuePath + "[1]", out var high)) return null;
                if (low is null || high is null)
                {
                    Error(valuePath, "'between' values can't be null.");
                    return null;
                }
                var sql = Sql.Raw(expr + " BETWEEN ");
                sql.AppendFormatted(low);
                sql.AppendLiteral(" AND ");
                sql.AppendFormatted(high);
                return sql;
            }

            case Operator.Contains or Operator.NotContains or Operator.StartsWith or Operator.EndsWith:
            {
                if (type != null && type != typeof(string))
                {
                    Error(path + ".operator", $"'{QuerySchema.CamelCase(c.Operator.ToString())}' only works on text fields; '{c.Field}' is {TypeName(type)}.");
                    return null;
                }
                if (c.Value is not { ValueKind: JsonValueKind.String } text)
                {
                    Error(valuePath, "A text value is required.");
                    return null;
                }

                var escaped = _dialect.EscapeLikePattern(text.GetString()!);
                var pattern = c.Operator switch
                {
                    Operator.StartsWith => escaped + "%",
                    Operator.EndsWith => "%" + escaped,
                    _ => "%" + escaped + "%",
                };
                var (lhs, folded) = Fold(expr, pattern, c.CaseInsensitive);
                var sql = Sql.Raw(lhs + (c.Operator == Operator.NotContains ? " NOT LIKE " : " LIKE "));
                sql.AppendFormatted(folded);
                sql.AppendLiteral($" ESCAPE '{_dialect.LikeEscapeCharacter}'");
                return sql;
            }

            default:
                Error(path + ".operator", $"Unknown operator '{c.Operator}'.");
                return null;
        }
    }

    private static Sql Compare(string expr, string op, object? value)
    {
        var sql = Sql.Raw($"{expr} {op} ");
        sql.AppendFormatted(value);
        return sql;
    }

    private static (string Expr, object? Value) Fold(string expr, object? value, bool caseInsensitive) =>
        caseInsensitive && value is string s ? ($"LOWER({expr})", s.ToLowerInvariant()) : (expr, value);

    private bool TryScalar(Condition c, Type? type, string path, out object? value)
    {
        value = null;
        // System.Text.Json reads "value": null as a missing JsonElement?, so absent and null look the same here.
        if (c.Value is not { } element || element.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            Error(path, $"A value is required for '{QuerySchema.CamelCase(c.Operator.ToString())}'. " +
                        "To compare with null, use the isNull or isNotNull operator.");
            return false;
        }
        if (element.ValueKind is JsonValueKind.Array or JsonValueKind.Object)
        {
            Error(path, "A single value is required. Use 'in' to compare with a list.");
            return false;
        }
        return TryConvert(element, type, path, out value);
    }

    private bool TryArray(Condition c, string path, out List<JsonElement> items)
    {
        items = [];
        if (c.Value is not { ValueKind: JsonValueKind.Array } array)
        {
            Error(path, $"An array value is required for '{QuerySchema.CamelCase(c.Operator.ToString())}'.");
            return false;
        }
        items = array.EnumerateArray().ToList();
        return true;
    }

    private bool TryConvert(JsonElement element, Type? type, string path, out object? value)
    {
        value = null;
        if (element.ValueKind == JsonValueKind.Null) return true;

        if (type != null)
        {
            try
            {
                value = element.Deserialize(type, ValueOptions);
                return true;
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException or FormatException or InvalidOperationException)
            {
                var raw = element.GetRawText();
                Error(path, $"Expected {TypeName(type)}, but got {(raw.Length > 40 ? raw[..37] + "..." : raw)}.");
                return false;
            }
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.String: value = element.GetString(); return true;
            case JsonValueKind.True: value = true; return true;
            case JsonValueKind.False: value = false; return true;
            case JsonValueKind.Number:
                value = element.TryGetInt64(out var l) ? l : element.TryGetDecimal(out var d) ? d : element.GetDouble();
                return true;
            default:
                Error(path, "Expected a text, number or boolean value.");
                return false;
        }
    }

    // ---- Helpers ----

    private string Q(string identifier) => _dialect.QuoteIdentifier(identifier);

    private string TableSql(TableDefinition table) => _dialect.QuoteTableName(table.DbTable, table.DbSchema);

    private static string AggregateSql(Aggregate aggregate, string expr) => aggregate switch
    {
        Aggregate.Count => $"COUNT({expr})",
        Aggregate.CountDistinct => $"COUNT(DISTINCT {expr})",
        Aggregate.Sum => $"SUM({expr})",
        Aggregate.Avg => $"AVG({expr})",
        Aggregate.Min => $"MIN({expr})",
        _ => $"MAX({expr})",
    };

    private static string? ComparisonSql(Operator op) => op switch
    {
        Operator.Eq => "=",
        Operator.Neq => "<>",
        Operator.Gt => ">",
        Operator.Gte => ">=",
        Operator.Lt => "<",
        Operator.Lte => "<=",
        _ => null,
    };

    private void Error(string path, string message) => _errors.Add(new QueryError(path, message));

    [GeneratedRegex("^(?!__)[A-Za-z_][A-Za-z0-9_]{0,63}$")]
    private static partial Regex ValidName();

    private static bool IsValidName(string name) => ValidName().IsMatch(name);

    private static string TypeName(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;
        return underlying switch
        {
            _ when underlying == typeof(string) => "text",
            _ when underlying == typeof(bool) => "a boolean",
            _ when underlying == typeof(DateTime) || underlying == typeof(DateTimeOffset) => "a date/time (ISO 8601)",
            _ when underlying == typeof(DateOnly) => "a date (yyyy-MM-dd)",
            _ when underlying == typeof(TimeOnly) || underlying == typeof(TimeSpan) => "a time (HH:mm:ss)",
            _ when underlying == typeof(Guid) => "a GUID",
            _ when underlying.IsEnum => $"one of: {string.Join(", ", Enum.GetNames(underlying))}",
            _ when underlying == typeof(int) || underlying == typeof(long) || underlying == typeof(short) || underlying == typeof(byte) => "a whole number",
            _ when underlying == typeof(decimal) || underlying == typeof(double) || underlying == typeof(float) => "a number",
            _ => underlying.Name,
        };
    }

    /// <summary>" Did you mean 'x'?" from the allowed names only, so undeclared columns are never revealed.</summary>
    private static string Hint(string name, IEnumerable<string> candidates)
    {
        var best = candidates
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(c => (Name: c, Distance: Distance(name.ToLowerInvariant(), c.ToLowerInvariant())))
            .Where(x => x.Distance <= Math.Max(1, name.Length / 3))
            .OrderBy(x => x.Distance)
            .FirstOrDefault();
        return best.Name != null ? $" Did you mean '{best.Name}'?" : "";
    }

/// <summary>Edit distance where swapping two adjacent letters ("nmae") counts as one edit, like other typos.</summary>
    private static int Distance(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (var j = 0; j <= b.Length; j++) d[0, j] = j;

        for (var i = 1; i <= a.Length; i++)
        {
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                    d[i, j] = Math.Min(d[i, j], d[i - 2, j - 2] + 1);
            }
        }
        return d[a.Length, b.Length];
    }
}
