using System.Text.Json;
using SqlWright.DynamicQuery;

namespace SqlWright.Tests;

public class DynamicQueryTests : Database
{
    private static readonly QuerySchema Schema = new QuerySchema()
        .Table("customers", t => t
            .From("dq_customers")
            .Key("id")
            .Column<long>("id", "Id")
            .Column<string>("name", "Name")
            .Column<string>("city", "City"))
        .Table("orders", t => t
            .From("dq_orders")
            .Key("id")
            .Column<long>("id", "Id")
            .Column<long>("customerId", "CustomerId")
            .Column<double>("total", "Total")
            .Column<string>("status", "Status")
            .Column<string>("placedAt", "PlacedAt"));

    // Every query only sees tenant 1, whatever the client asks for.
    private static readonly DynamicQueryOptions Tenant1 = new DynamicQueryOptions()
        .Filter("customers", c => $"{c["TenantId"]} = {1}")
        .Filter("orders", o => $"{o["TenantId"]} = {1}");

    public DynamicQueryTests()
    {
        Connection.Execute("""
            CREATE TABLE dq_customers (Id INTEGER PRIMARY KEY, Name TEXT NOT NULL, City TEXT NULL, TenantId INTEGER NOT NULL);
            CREATE TABLE dq_orders (Id INTEGER PRIMARY KEY, CustomerId INTEGER NOT NULL, Total REAL NOT NULL,
                                    Status TEXT NOT NULL, PlacedAt TEXT NOT NULL, TenantId INTEGER NOT NULL);
            INSERT INTO dq_customers VALUES
                (1, 'Ada', 'London', 1), (2, 'Grace', NULL, 1), (3, 'Linus', 'Helsinki', 1),
                (4, 'Margaret', 'London', 1), (5, 'Mallory', 'Paris', 2);
            INSERT INTO dq_orders VALUES
                (1, 1, 100, 'paid', '2026-01-05', 1), (2, 1, 50, 'open', '2026-02-01', 1),
                (3, 3, 75.5, 'paid', '2026-01-20', 1), (4, 4, 20, 'paid', '2026-03-01', 1),
                (5, 5, 999, 'paid', '2026-01-01', 2);
            """);
    }

    private static QueryRequest Parse(string json) => JsonSerializer.Deserialize<QueryRequest>(json, DynamicQueryJson.Options)!;

    private QueryResponse Run(string json) => Connection.QueryDynamic(Parse(json), Schema, Tenant1);

    private static List<object?> Column(QueryResponse response, string key) => response.Data.Select(r => r[key]).ToList();

    [Fact]
    public void Deserializes_nested_groups_and_camel_case_enums()
    {
        var request = Parse("""
            {
              "table": "customers",
              "where": { "logic": "or", "conditions": [
                { "field": "name", "operator": "eq", "value": "Ada" },
                { "not": true, "conditions": [ { "field": "city", "operator": "IsNull" } ] }
              ] },
              "orderBy": [ { "field": "name", "direction": "desc", "nulls": "last" } ]
            }
            """);

        var group = request.Where!;
        Assert.Equal(Logic.Or, group.Logic);
        Assert.IsType<Condition>(group.Conditions[0]);
        var nested = Assert.IsType<ConditionGroup>(group.Conditions[1]);
        Assert.True(nested.Not);
        Assert.Equal(Operator.IsNull, ((Condition)nested.Conditions[0]).Operator);
        Assert.Equal(SortDirection.Desc, request.OrderBy[0].Direction);
        Assert.Contains("\"operator\":\"isNull\"", JsonSerializer.Serialize(nested.Conditions[0], DynamicQueryJson.Options));
    }

    [Fact]
    public void Default_select_returns_only_exposed_columns_and_applies_row_filter()
    {
        var result = Run("""{ "table": "customers", "orderBy": [ { "field": "id" } ] }""");

        Assert.Equal(new object?[] { 1L, 2L, 3L, 4L }, Column(result, "id")); // tenant 2's customer is filtered out
        Assert.Equal(new[] { "id", "name", "city" }, result.Data[0].Keys);    // TenantId is not exposed
    }

    [Fact]
    public void Nested_logic_with_case_insensitive_and_null_checks()
    {
        var result = Run("""
            {
              "table": "customers",
              "select": [ { "field": "name" } ],
              "where": { "logic": "or", "conditions": [
                { "field": "name", "operator": "eq", "value": "ADA", "caseInsensitive": true },
                { "field": "city", "operator": "isNull" },
                { "logic": "and", "not": true, "conditions": [
                  { "field": "city", "operator": "neq", "value": "Helsinki" },
                  { "field": "id", "operator": "gt", "value": 0 }
                ] }
              ] },
              "orderBy": [ { "field": "name" } ]
            }
            """);

        // Ada (name), Grace (null city), Linus (NOT (city <> Helsinki AND ...))
        Assert.Equal(new object?[] { "Ada", "Grace", "Linus" }, Column(result, "name"));
    }

    [Fact]
    public void In_not_in_between_and_empty_lists()
    {
        Assert.Equal(new object?[] { 1L, 3L },
            Column(Run("""{ "table": "customers", "where": { "conditions": [ { "field": "id", "operator": "in", "value": [1, 3, 5] } ] }, "orderBy": [ { "field": "id" } ] }"""), "id"));
        Assert.Equal(new object?[] { 2L, 4L },
            Column(Run("""{ "table": "customers", "where": { "conditions": [ { "field": "id", "operator": "notIn", "value": ["1", "3"] } ] }, "orderBy": [ { "field": "id" } ] }"""), "id"));
        Assert.Equal(new object?[] { 2L, 3L },
            Column(Run("""{ "table": "orders", "where": { "conditions": [ { "field": "total", "operator": "between", "value": [50, 80] } ] }, "orderBy": [ { "field": "id" } ] }"""), "id"));
        Assert.Empty(Run("""{ "table": "customers", "where": { "conditions": [ { "field": "id", "operator": "in", "value": [] } ] } }""").Data);
    }

    [Fact]
    public void Like_operators_escape_wildcards()
    {
        Connection.Execute("INSERT INTO dq_customers VALUES (6, '100%_pure', NULL, 1), (7, '100 pure', NULL, 1)");

        var contains = Run("""{ "table": "customers", "where": { "conditions": [ { "field": "name", "operator": "contains", "value": "%_" } ] } }""");
        Assert.Equal(new object?[] { "100%_pure" }, Column(contains, "name"));

        var starts = Run("""{ "table": "customers", "where": { "conditions": [ { "field": "name", "operator": "startsWith", "value": "Mar" } ] } }""");
        Assert.Equal(new object?[] { "Margaret" }, Column(starts, "name"));

        var notContains = Run("""{ "table": "customers", "where": { "conditions": [ { "field": "name", "operator": "notContains", "value": "a" } ] }, "orderBy": [ { "field": "id" } ] }""");
        Assert.Equal(new object?[] { "Linus", "100%_pure", "100 pure" }, Column(notContains, "name"));
    }

    [Fact]
    public void Join_group_having_and_sort_by_aggregate_alias()
    {
        var result = Run("""
            {
              "table": "orders", "alias": "o",
              "joins": [ { "type": "inner", "table": "customers", "alias": "c",
                           "on": [ { "left": "o.customerId", "right": "c.id" } ] } ],
              "select": [
                { "field": "c.name", "alias": "customer" },
                { "field": "o.total", "aggregate": "sum", "alias": "revenue" },
                { "field": "*", "aggregate": "count", "alias": "orders" }
              ],
              "groupBy": [ "c.name" ],
              "having": { "conditions": [ { "field": "o.total", "aggregate": "sum", "operator": "gt", "value": 60 } ] },
              "orderBy": [ { "field": "revenue", "direction": "desc" } ]
            }
            """);

        Assert.Equal(new object?[] { "Ada", "Linus" }, Column(result, "customer")); // Mallory (tenant 2) excluded via join filter
        Assert.Equal(new object?[] { 150.0, 75.5 }, Column(result, "revenue"));
        Assert.Equal(new object?[] { 2L, 1L }, Column(result, "orders"));
    }

    [Fact]
    public void Left_join_keeps_unmatched_rows_with_row_filter_in_on_clause()
    {
        var result = Run("""
            {
              "table": "customers",
              "joins": [ { "type": "left", "table": "orders", "on": [ { "left": "customers.id", "right": "orders.customerId" } ] } ],
              "select": [ { "field": "customers.name" }, { "field": "orders.id", "aggregate": "count", "alias": "orderCount" } ],
              "groupBy": [ "customers.name" ],
              "orderBy": [ { "field": "customers.name" } ]
            }
            """);

        Assert.Equal(new object?[] { "Ada", "Grace", "Linus", "Margaret" }, Column(result, "customers.name"));
        Assert.Equal(new object?[] { 2L, 0L, 1L, 1L }, Column(result, "orderCount"));
    }

    [Fact]
    public void Offset_pagination_with_total_count()
    {
        var page1 = Run("""{ "table": "customers", "orderBy": [ { "field": "name" } ], "pagination": { "page": 1, "pageSize": 3 }, "includeTotalCount": true }""");
        Assert.Equal(new object?[] { "Ada", "Grace", "Linus" }, Column(page1, "name"));
        Assert.Equal(4, page1.Page.TotalCount);
        Assert.Equal(2, page1.Page.TotalPages);
        Assert.True(page1.Page.HasNextPage);

        var page2 = Run("""{ "table": "customers", "orderBy": [ { "field": "name" } ], "pagination": { "page": 2, "pageSize": 3 } }""");
        Assert.Equal(new object?[] { "Margaret" }, Column(page2, "name"));
        Assert.False(page2.Page.HasNextPage);
        Assert.Null(page2.Page.TotalCount);
    }

    [Theory]
    [InlineData("asc", null, new long[] { 3, 1, 4, 2 })]    // nulls last by default when ascending
    [InlineData("desc", null, new long[] { 2, 1, 4, 3 })]   // ...and first when descending
    [InlineData("asc", "first", new long[] { 2, 3, 1, 4 })]
    [InlineData("desc", "last", new long[] { 1, 4, 3, 2 })]
    public void Cursor_pagination_walks_every_row_once_with_ties_and_nulls(string direction, string? nulls, long[] expected)
    {
        var nullsJson = nulls == null ? "" : $", \"nulls\": \"{nulls}\"";
        var seen = new List<long>();
        string? after = null;
        var pages = 0;

        do
        {
            var afterJson = after == null ? "" : $", \"after\": \"{after}\"";
            var page = Run($$"""
                {
                  "table": "customers",
                  "orderBy": [ { "field": "city", "direction": "{{direction}}"{{nullsJson}} } ],
                  "pagination": { "mode": "cursor", "pageSize": 1{{afterJson}} },
                  "includeTotalCount": true
                }
                """);

            Assert.Equal(4, page.Page.TotalCount);       // total ignores the cursor position
            Assert.DoesNotContain(page.Data[0].Keys, k => k.StartsWith("__"));
            seen.AddRange(page.Data.Select(r => (long)r["id"]!));
            after = page.Page.NextCursor;
            Assert.Equal(after != null, page.Page.HasNextPage);
        }
        while (after != null && ++pages < 10);

        Assert.Equal(expected, seen);
    }

    [Fact]
    public void Cursor_from_a_different_sort_is_rejected()
    {
        var first = Run("""{ "table": "customers", "orderBy": [ { "field": "name" } ], "pagination": { "mode": "cursor", "pageSize": 1 } }""");

        var ex = Assert.Throws<DynamicQueryException>(() => Run($$"""
            { "table": "customers", "orderBy": [ { "field": "city" } ], "pagination": { "mode": "cursor", "pageSize": 1, "after": "{{first.Page.NextCursor}}" } }
            """));
        Assert.Equal("pagination.after", Assert.Single(ex.Errors).Path);

        var garbage = Assert.Throws<DynamicQueryException>(() => Run("""
            { "table": "customers", "pagination": { "mode": "cursor", "after": "not-a-cursor" } }
            """));
        Assert.Equal("pagination.after", Assert.Single(garbage.Errors).Path);
    }

    [Fact]
    public void Validation_reports_every_problem_with_paths_and_suggestions()
    {
        var ex = Assert.Throws<DynamicQueryException>(() => Run("""
            {
              "table": "customers",
              "select": [ { "field": "nmae" }, { "field": "TenantId" }, { "field": "name; DROP TABLE dq_customers" } ],
              "where": { "conditions": [
                { "field": "id", "operator": "eq", "value": "abc" },
                { "field": "name", "aggregate": "count", "operator": "gt", "value": 1 },
                { "field": "id", "operator": "contains", "value": "1" },
                { "field": "city", "operator": "eq", "value": null }
              ] },
              "pagination": { "pageSize": 1000 }
            }
            """));

        var errors = ex.ToDictionary();
        Assert.Equal("Unknown field 'nmae'. Did you mean 'name'?", errors["select[0].field"].Single());
        Assert.Equal("Unknown field 'TenantId'.", errors["select[1].field"].Single());         // undeclared, and never suggested
        Assert.StartsWith("Unknown field 'name; DROP TABLE", errors["select[2].field"].Single());
        Assert.Equal("Expected a whole number, but got \"abc\".", errors["where.conditions[0].value"].Single());
        Assert.Contains("only allowed in 'having'", errors["where.conditions[1].aggregate"].Single());
        Assert.Contains("only works on text fields", errors["where.conditions[2].operator"].Single());
        Assert.Contains("isNull", errors["where.conditions[3].value"].Single());
        Assert.Equal("Must be between 1 and 100.", errors["pagination.pageSize"].Single());
        Assert.Equal(3, Connection.ExecuteScalar<int>("SELECT COUNT(*) FROM dq_customers WHERE Id IN (1, 2, 3)"));
    }

    [Fact]
    public void Unknown_tables_and_ambiguous_fields_are_explained()
    {
        var unknown = Assert.Throws<DynamicQueryException>(() => Run("""{ "table": "customer" }"""));
        Assert.Equal("Unknown table 'customer'. Did you mean 'customers'?", unknown.Errors.Single().Message);

        var ambiguous = Assert.Throws<DynamicQueryException>(() => Run("""
            { "table": "orders", "joins": [ { "table": "customers", "on": [ { "left": "customerId", "right": "customers.id" } ] } ],
              "select": [ { "field": "name" }, { "field": "customers.id" } ], "where": { "conditions": [ { "field": "o.id", "operator": "eq", "value": 1 } ] } }
            """));
        Assert.Equal("Unknown table or alias 'o'. Use one of: orders, customers.", ambiguous.Errors.Single().Message);
    }

    [Fact]
    public void Limits_are_enforced()
    {
        var schema = new QuerySchema().Table("customers", t => t.From("dq_customers").Column<long>("id", "Id"));
        schema.Limits.MaxInValues = 2;
        schema.Limits.MaxOffset = 10;

        var ex = Assert.Throws<DynamicQueryException>(() => schema.Translate(Parse("""
            { "table": "customers", "where": { "conditions": [ { "field": "id", "operator": "in", "value": [1, 2, 3] } ] },
              "pagination": { "page": 5, "pageSize": 5 } }
            """), SqlDialect.Sqlite));

        Assert.Contains(ex.Errors, e => e.Path == "where.conditions[0].value" && e.Message.Contains("At most 2"));
        Assert.Contains(ex.Errors, e => e.Path == "pagination.page" && e.Message.Contains("cursor pagination"));
    }

    [Fact]
    public void Generates_dialect_specific_sql()
    {
        var request = Parse("""
            { "table": "customers", "select": [ { "field": "name" } ],
              "where": { "conditions": [ { "field": "name", "operator": "contains", "value": "[x]" } ] },
              "orderBy": [ { "field": "city", "nulls": "first" } ], "pagination": { "page": 3, "pageSize": 10 } }
            """);

        var sqlServer = Schema.Translate(request, SqlDialect.SqlServer).Data;
        Assert.Equal(
            "SELECT [t0].[Name] AS [name] FROM [dq_customers] [t0] WHERE ([t0].[Name] LIKE @p0 ESCAPE '!') " +
            "ORDER BY CASE WHEN [t0].[City] IS NULL THEN 0 ELSE 1 END, [t0].[City] ASC OFFSET @p1 ROWS FETCH NEXT @p2 ROWS ONLY",
            sqlServer.Text);
        Assert.Equal(new object?[] { "%![x]%", 20, 11 }, sqlServer.Values);

        var postgres = Schema.Translate(request, SqlDialect.PostgreSql).Data;
        Assert.EndsWith("\"t0\".\"City\" ASC LIMIT @p1 OFFSET @p2", postgres.Text);
        Assert.Equal(new object?[] { "%[x]%", 11, 20 }, postgres.Values);

        var fullJoin = Parse("""{ "table": "customers", "joins": [ { "type": "full", "table": "orders", "on": [ { "left": "customers.id", "right": "orders.customerId" } ] } ] }""");
        var mysql = Assert.Throws<DynamicQueryException>(() => Schema.Translate(fullJoin, SqlDialect.MySql));
        Assert.Equal("joins[0].type", mysql.Errors.Single().Path);
    }

    [Fact]
    public async Task Async_with_entity_based_schema()
    {
        var schema = new QuerySchema().Table<CrudTests.User>("users", t => t.Ignore(nameof(CrudTests.User.Email)));
        Connection.Insert(new CrudTests.User { FirstName = "Ada", Email = "secret@example.com", Age = 36 });

        var result = await Connection.QueryDynamicAsync(Parse("""
            { "table": "users", "where": { "conditions": [ { "field": "firstName", "operator": "startsWith", "value": "A" } ] } }
            """), schema);

        var row = Assert.Single(result.Data);
        Assert.Equal("Ada", row["firstName"]);
        Assert.False(row.ContainsKey("email"));
        Assert.Throws<DynamicQueryException>(() => Connection.QueryDynamic(Parse("""
            { "table": "users", "where": { "conditions": [ { "field": "email", "operator": "isNotNull" } ] } }
            """), schema));
    }
}
