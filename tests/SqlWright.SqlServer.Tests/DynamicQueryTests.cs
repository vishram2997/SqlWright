using System.Text.Json;
using SqlWright.DynamicQuery;

namespace SqlWright.SqlServer.Tests;

public class DynamicQueryTests : SqlServerTest
{
    private static readonly QuerySchema Schema = new QuerySchema()
        .Table("customers", t => t
            .From("DqCustomers", schema: "dbo")
            .Key("id")
            .Column<int>("id", "Id")
            .Column<string>("name", "Name")
            .Column<string>("city", "City")
            .Column<DateTime>("createdAt", "CreatedAt"))
        .Table("invoices", t => t
            .From("DqInvoices", schema: "dbo")
            .Key("id")
            .Column<int>("id", "Id")
            .Column<int>("customerId", "CustomerId")
            .Column<decimal>("amount", "Amount")
            .Column<string>("status", "Status")
            .Column<DateOnly>("issuedOn", "IssuedOn"))
        .Table<User>("users", t => t.Ignore(nameof(User.RowVer)).Ignore(nameof(User.Email)));

    private readonly DynamicQueryOptions _tenant1 = new DynamicQueryOptions()
        .Filter("customers", c => $"{c["TenantId"]} = {1}")
        .Filter("invoices", i => $"{i["TenantId"]} = {1}");

    private readonly Dictionary<string, int> _ids = new();

    public DynamicQueryTests(SqlServerFixture fixture) : base(fixture)
    {
        var customers = new (string Name, string? City, int Tenant, DateTime Created)[]
        {
            ("Ada", "London", 1, new DateTime(2026, 1, 1, 9, 0, 0)),
            ("Grace", null, 1, new DateTime(2026, 1, 2, 9, 0, 0)),
            ("Linus", "Helsinki", 1, new DateTime(2026, 1, 3, 9, 0, 0)),
            ("Margaret", "London", 1, new DateTime(2026, 1, 3, 9, 0, 0)),   // same CreatedAt as Linus: a tie
            ("[VIP] 100% Ünïcødé_Co", "Zürich", 1, new DateTime(2026, 1, 4, 9, 0, 0)),
            ("Mallory", "Paris", 2, new DateTime(2026, 1, 5, 9, 0, 0)),
        };
        foreach (var c in customers)
        {
            _ids[c.Name] = Connection.ExecuteScalar<int>(
                $"INSERT INTO dbo.DqCustomers (Name, City, TenantId, CreatedAt) VALUES ({c.Name}, {c.City}, {c.Tenant}, {c.Created}); SELECT CAST(SCOPE_IDENTITY() AS int)");
        }

        var invoices = new (string Customer, decimal Amount, string Status, DateTime Issued, int Tenant)[]
        {
            ("Ada", 100.25m, "paid", new DateTime(2026, 1, 5), 1),
            ("Ada", 49.75m, "open", new DateTime(2026, 2, 1), 1),
            ("Linus", 75.50m, "paid", new DateTime(2026, 1, 20), 1),
            ("Margaret", 20.00m, "paid", new DateTime(2026, 3, 1), 1),
            ("Mallory", 999.00m, "paid", new DateTime(2026, 1, 1), 2),
        };
        foreach (var i in invoices)
        {
            Connection.Execute(
                $"INSERT INTO dbo.DqInvoices (CustomerId, Amount, Status, IssuedOn, TenantId) VALUES ({_ids[i.Customer]}, {i.Amount}, {i.Status}, {i.Issued}, {i.Tenant})");
        }
    }

    private static QueryRequest Parse(string json) => JsonSerializer.Deserialize<QueryRequest>(json, DynamicQueryJson.Options)!;

    private QueryResponse Run(string json) => Connection.QueryDynamic(Parse(json), Schema, _tenant1);

    private static List<object?> Column(QueryResponse response, string key) => response.Data.Select(r => r[key]).ToList();

    [Fact]
    public void Join_group_having_on_decimal_with_offset_fetch_and_total()
    {
        var result = Run("""
            {
              "table": "invoices", "alias": "i",
              "joins": [ { "table": "customers", "alias": "c", "on": [ { "left": "i.customerId", "right": "c.id" } ] } ],
              "select": [
                { "field": "c.name", "alias": "customer" },
                { "field": "i.amount", "aggregate": "sum", "alias": "revenue" },
                { "field": "i.id", "aggregate": "count", "alias": "invoiceCount" }
              ],
              "groupBy": [ "c.name" ],
              "having": { "conditions": [ { "field": "i.amount", "aggregate": "sum", "operator": "gte", "value": 50.5 } ] },
              "orderBy": [ { "field": "revenue", "direction": "desc" } ],
              "pagination": { "page": 1, "pageSize": 10 },
              "includeTotalCount": true
            }
            """);

        Assert.Equal(new object?[] { "Ada", "Linus" }, Column(result, "customer"));
        Assert.Equal(new object?[] { 150.00m, 75.50m }, Column(result, "revenue"));
        Assert.Equal(new object?[] { 2, 1 }, Column(result, "invoiceCount"));
        Assert.Equal(2, result.Page.TotalCount);   // COUNT over the grouped derived table
        Assert.Equal(1, result.Page.TotalPages);
    }

    [Fact]
    public void Like_escapes_sql_server_brackets_percent_and_underscore()
    {
        var result = Run("""
            { "table": "customers", "select": [ { "field": "name" } ],
              "where": { "logic": "or", "conditions": [
                { "field": "name", "operator": "startsWith", "value": "[VIP] 100%" },
                { "field": "name", "operator": "endsWith", "value": "ü_co", "caseInsensitive": true }
              ] } }
            """);

        Assert.Equal(new object?[] { "[VIP] 100% Ünïcødé_Co" }, Column(result, "name"));
        Assert.Empty(Run("""{ "table": "customers", "where": { "conditions": [ { "field": "name", "operator": "contains", "value": "[V" } ] } }""").Data.Where(r => (string)r["name"]! != "[VIP] 100% Ünïcødé_Co"));
    }

    [Fact]
    public void Dates_convert_from_json_and_filter_correctly()
    {
        var january = Run("""
            { "table": "invoices", "select": [ { "field": "id" }, { "field": "issuedOn" } ],
              "where": { "conditions": [ { "field": "issuedOn", "operator": "between", "value": ["2026-01-01", "2026-01-31"] } ] },
              "orderBy": [ { "field": "issuedOn" } ] }
            """);
        Assert.Equal(new object?[] { new DateTime(2026, 1, 5), new DateTime(2026, 1, 20) }, Column(january, "issuedOn"));

        var recent = Run("""
            { "table": "customers", "select": [ { "field": "name" } ],
              "where": { "conditions": [ { "field": "createdAt", "operator": "gte", "value": "2026-01-03T09:00:00" } ] },
              "orderBy": [ { "field": "name" } ] }
            """);
        Assert.Equal(new object?[] { "[VIP] 100% Ünïcødé_Co", "Linus", "Margaret" }, Column(recent, "name"));
    }

    [Theory]
    [InlineData("createdAt", "asc")]
    [InlineData("createdAt", "desc")]
    [InlineData("city", "asc")]
    [InlineData("city", "desc")]
    public void Cursor_pagination_matches_a_full_sorted_read(string field, string direction)
    {
        // The reference order: one unpaged query with the same sort plus the key as tiebreaker.
        var nulls = direction == "asc" ? "last" : "first";
        var expected = Column(Run($$"""
            { "table": "customers", "orderBy": [ { "field": "{{field}}", "direction": "{{direction}}", "nulls": "{{nulls}}" }, { "field": "id" } ],
              "pagination": { "pageSize": 100 } }
            """), "id");

        var seen = new List<object?>();
        string? after = null;
        var guard = 0;
        do
        {
            var afterJson = after == null ? "" : $", \"after\": \"{after}\"";
            var page = Run($$"""
                { "table": "customers", "orderBy": [ { "field": "{{field}}", "direction": "{{direction}}" } ],
                  "pagination": { "mode": "cursor", "pageSize": 2{{afterJson}} } }
                """);
            seen.AddRange(Column(page, "id"));
            after = page.Page.NextCursor;
        }
        while (after != null && ++guard < 10);

        Assert.Equal(5, expected.Count);   // tenant 2 filtered out
        Assert.Equal(expected, seen);
    }

    [Fact]
    public void Full_and_left_joins_with_tenant_filter_in_on_clause()
    {
        var result = Run("""
            { "table": "customers", "alias": "c",
              "joins": [ { "type": "full", "table": "invoices", "alias": "i", "on": [ { "left": "c.id", "right": "i.customerId" } ] } ],
              "select": [ { "field": "c.name" }, { "field": "i.id", "aggregate": "count", "alias": "n" } ],
              "groupBy": [ "c.name" ],
              "orderBy": [ { "field": "c.name" } ] }
            """);

        Assert.Equal(new object?[] { "[VIP] 100% Ünïcødé_Co", "Ada", "Grace", "Linus", "Margaret" }, Column(result, "c.name"));
        Assert.Equal(new object?[] { 0, 2, 0, 1, 1 }, Column(result, "n"));
    }

    [Fact]
    public void Distinct_without_order_by_still_pages_on_sql_server()
    {
        // No ORDER BY: SQL Server needs ORDER BY (SELECT NULL) before OFFSET/FETCH.
        var result = Run("""
            { "table": "customers", "distinct": true, "select": [ { "field": "city" } ],
              "pagination": { "page": 1, "pageSize": 2 }, "includeTotalCount": true }
            """);

        Assert.Equal(2, result.Data.Count);
        Assert.True(result.Page.HasNextPage);
        Assert.Equal(4, result.Page.TotalCount); // London, Helsinki, Zürich, NULL
    }

    [Fact]
    public async Task Entity_based_table_hides_ignored_columns()
    {
        Connection.Insert(new User { UserName = "ada", Email = "secret@example.com", Balance = 12.5m, Status = UserStatus.Active });
        Connection.Insert(new User { UserName = "bob", Balance = 3m });

        var result = await Connection.QueryDynamicAsync(Parse("""
            { "table": "users", "where": { "conditions": [ { "field": "status", "operator": "eq", "value": "Active" } ] } }
            """), Schema);

        var row = Assert.Single(result.Data);
        Assert.Equal("ada", row["userName"]);
        Assert.Equal(12.50m, row["balance"]);
        Assert.False(row.ContainsKey("email"));
        Assert.False(row.ContainsKey("rowVer"));

        var ex = Assert.Throws<DynamicQueryException>(() => Connection.QueryDynamic(Parse("""
            { "table": "users", "select": [ { "field": "email" } ] }
            """), Schema));
        Assert.Equal("Unknown field 'email'.", ex.Errors.Single().Message);
    }

    [Fact]
    public void Tenant_filter_cannot_be_escaped_by_the_client()
    {
        var result = Run("""
            { "table": "customers", "where": { "logic": "or", "conditions": [
                { "field": "name", "operator": "eq", "value": "Mallory" },
                { "field": "id", "operator": "gt", "value": 0 } ] } }
            """);

        Assert.DoesNotContain("Mallory", Column(result, "name"));
        Assert.Equal(5, result.Data.Count);
    }
}
