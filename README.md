# SqlWright

A fast, lightweight micro-ORM for .NET. You write the SQL; SqlWright maps the results to your objects.

- **Injection-safe string interpolation**: `$"... WHERE Id = {id}"` becomes a parameterised query
- **Built-in CRUD**: `Get`, `GetAll`, `Insert`, `Update`, `Delete` with SQL generated per database
- **Helpful errors**: failures name the column, member, value and types, and suggest fixes
- Extension methods on any ADO.NET `IDbConnection` (SQL Server, PostgreSQL, MySQL, SQLite, ...)
- Compiled, cached mappers (expression trees), no reflection per row
- Classes, structs, **records / constructor mapping**, scalars, enums, and `dynamic` rows
- Parameters from anonymous objects, POCOs, dictionaries, or `SqlParameters` (output params, explicit `DbType`)
- `IN @ids` list expansion
- Sync and async APIs with `CancellationToken`, plus `IAsyncEnumerable` streaming on .NET 8+
- Multiple result sets
- Opens and closes the connection for you if it is closed
- Targets `netstandard2.0` and `net8.0`, no dependencies

## Install

```
dotnet add package SqlWright
```

## Querying

```csharp
using SqlWright;

public class User
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string? Email { get; set; }
}

using var connection = new SqlConnection(connectionString);

IEnumerable<User> users = connection.Query<User>($"SELECT * FROM Users WHERE Age > {minAge}");
User user = connection.QuerySingle<User>($"SELECT * FROM Users WHERE Id = {id}");
User? maybe = connection.QueryFirstOrDefault<User>($"SELECT * FROM Users WHERE Email = {email}");
int count = connection.ExecuteScalar<int>($"SELECT COUNT(*) FROM Users");
connection.Execute($"UPDATE Users SET Name = {name} WHERE Id = {id}");
```

## Interpolated SQL

Every interpolated value becomes a parameter, so interpolation is safe from SQL injection:

```csharp
var name = "x' OR '1'='1";
connection.Query<User>($"SELECT * FROM Users WHERE Name = {name}");
// SELECT * FROM Users WHERE Name = @p0      (@p0 = "x' OR '1'='1")
```

Lists expand into `IN` clauses, and `null` is sent as `NULL`:

```csharp
connection.Query<User>($"SELECT * FROM Users WHERE Id IN {ids}");
// ... WHERE Id IN (@p0_1, @p0_2, @p0_3)
```

Build queries in pieces. A `Sql` embedded in another brings its parameters with it:

```csharp
var conditions = new List<Sql> { $"Status = {status}" };
if (minAge is not null) conditions.Add($"Age >= {minAge}");
if (search is not null) conditions.Add($"Name LIKE {search + "%"}");

Sql query = $"SELECT * FROM Users WHERE {Sql.Join(" AND ", conditions)}";
query.Append($" ORDER BY Name");

var results = connection.Query<User>(query);
Console.WriteLine(query.Text);   // the SQL with @p0, @p1, ... for logging
```

To insert trusted text such as a table name verbatim, use `:raw`. **Never use `:raw` with user input.**

```csharp
connection.Query<User>($"SELECT * FROM {tableName:raw} WHERE Id = {id}");
```

> **String constants in holes.** If *every* `{...}` in an interpolated string is a string constant, e.g.
> `$"... WHERE Name = {"ada"}"`, the C# compiler folds it into a plain constant string before SqlWright sees
> it, so the text is inlined rather than parameterised (the query then fails with an invalid-column error).
> This only affects literals written in your code, never variables, so it is not an injection risk. Use a
> variable, or target-type it: `Sql q = $"... {"ada"}";`.
>
> A plain `string` (not an interpolated one) still works the Dapper way, with a parameter object:
> `connection.Query<User>("SELECT * FROM Users WHERE Id = @id", new { id })`.
> When you pass an interpolated string *and* a parameter object, the string is treated as plain text.

## CRUD

```csharp
using SqlWright.Mapping;

[Table("Users")]
public class User
{
    public int Id { get; set; }                         // key by convention (Id or UserId)
    [Column("user_name")] public string Name { get; set; } = "";
    public string? Email { get; set; }
    [Computed] public DateTime CreatedAt { get; set; }  // filled in by the database, never written
}

var user = new User { Name = "Ada" };
connection.Insert(user);                // user.Id now holds the generated identity
User? loaded = connection.Get<User>(user.Id);
IEnumerable<User> all = connection.GetAll<User>();

user.Email = "ada@example.com";
connection.Update(user);                // updates every non-key column; returns true if a row changed

connection.Delete(user);
connection.DeleteById<User>(42);
connection.InsertAll(manyUsers);
```

All of these have `Async` versions.

**Keys.** The key is the member marked `[Key]`, or one named `Id` or `{TypeName}Id`. A single integer key is
assumed to be database-generated (identity / auto-increment / serial), so `Insert` leaves it out and writes the
generated value back to your object. Other keys (e.g. `Guid`) are inserted as given. Override with
`[Key(DatabaseGenerated = true/false)]`.

**Composite keys.** Mark each with `[Key]` and pass an object for lookups:
`connection.Get<OrderLine>(new { OrderId = 1, LineNo = 2 })`.

**Naming.** Without attributes, tables and columns are named after the type and its members.
For snake_case databases:

```csharp
SqlWrightSettings.NamingStyle = NamingStyle.SnakeCase;          // FirstName -> first_name, OrderLine -> order_line
SqlWrightSettings.TableNameResolver = type => type.Name + "s";  // optional: custom table names
```

DataAnnotations attributes (`[Table]`, `[Column]`, `[Key]`, `[NotMapped]`, `[DatabaseGenerated]`) work too.

**Databases.** SQL is generated for the database detected from your connection type:

| Connection | Dialect |
| --- | --- |
| `SqlConnection` (Microsoft.Data.SqlClient / System.Data.SqlClient) | `SqlDialect.SqlServer` |
| `NpgsqlConnection` | `SqlDialect.PostgreSql` |
| `SqliteConnection` (Microsoft.Data.Sqlite / System.Data.SQLite) | `SqlDialect.Sqlite` |
| `MySqlConnection` (MySqlConnector / MySql.Data) | `SqlDialect.MySql` |

For other or wrapped connections (e.g. a profiler), register the dialect once at startup:

```csharp
SqlWrightSettings.RegisterDialect<ProfiledDbConnection>(SqlDialect.SqlServer);
```

Subclass `SqlDialect` to support another database.

## Helpful errors

SqlWright tells you exactly what went wrong and how to fix it:

```
MappingException: Error mapping column 'Age' (ordinal 3) to User.Age (Int32):
cannot convert 'thirty' of type String. Input string was not in a correct format.
```

```
MappingException: None of the columns ('first_name') match a settable property or field of User.
Did you mean 'FirstName'? Set SqlWrightSettings.MatchNamesWithUnderscores = true, or add [Column("first_name")] to it.
```

```
MappingException: Cannot create PersonRecord: it has no public parameterless constructor, and no public
constructor has a column for every parameter. Columns: 'Id', 'FirstNam'.
Constructors tried:
  (Id, FirstName): no column for 'FirstName' (closest column: 'FirstNam')
```

```
SqlWrightException: Single<User> expected exactly one row, but the query returned more than one.
Use First/FirstOrDefault if you only need the first row, or tighten the WHERE clause.
```

`MappingException` exposes `ColumnName`, `ColumnOrdinal`, `MemberName` and `TargetType`. All SqlWright errors
derive from `SqlWrightException`, which derives from `InvalidOperationException`. Errors from the database
itself are never wrapped, so you can still catch `SqlException` and friends.

Turn on **strict mapping** in development and tests to catch typos and schema drift. It makes a column that
matches no member an error instead of being silently skipped:

```csharp
SqlWrightSettings.StrictMapping = true;
// MappingException: Column 'FirstNmae' does not match any settable property or field of User. Did you mean 'FirstName'?
```

## Dynamic queries from an API (`SqlWright.DynamicQuery`)

```
dotnet add package SqlWright.DynamicQuery
```

Let API clients filter, sort, join, group and page data with a JSON request, without writing an endpoint per
query and without opening a hole for SQL injection or data leaks.

**1. Declare what clients may query** (once, at startup). Nothing outside this allowlist can be queried, and
the generated SQL only contains identifiers from here. Every client value is a parameter.

```csharp
using SqlWright.DynamicQuery;

var schema = new QuerySchema()
    .Table<Customer>("customers", t => t.Ignore(nameof(Customer.PasswordHash)))  // from your entity mapping
    .Table("invoices", t => t                                                       // or by hand
        .From("Invoices", schema: "dbo")
        .Key("id")
        .Column<int>("id", "Id")
        .Column<int>("customerId", "CustomerId")
        .Column<decimal>("amount", "Amount")
        .Column<DateOnly>("issuedOn", "IssuedOn"));

schema.Limits.MaxPageSize = 100;   // also MaxJoins, MaxConditions, MaxNestingDepth, MaxInValues, MaxOffset, ...
```

**2. Expose an endpoint.** Row filters (tenant, ownership) are added server-side and can't be bypassed: they go
in `WHERE` for the main table and in the `ON` clause for joined tables.

```csharp
app.MapPost("/query", async (QueryRequest request, SqlConnection db, ClaimsPrincipal user) =>
{
    var tenantId = int.Parse(user.FindFirst("tenant")!.Value);
    var options = new DynamicQueryOptions()
        .Filter("customers", c => $"{c["TenantId"]} = {tenantId}")
        .Filter("invoices", i => $"{i["TenantId"]} = {tenantId}");

    try
    {
        return Results.Ok(await db.QueryDynamicAsync(request, schema, options));
    }
    catch (DynamicQueryException ex)
    {
        return Results.ValidationProblem(ex.ToDictionary());   // 400 with every problem, by JSON path
    }
});
```

**3. Clients send requests:**

```json
{
  "table": "invoices", "alias": "i",
  "joins": [ { "type": "inner", "table": "customers", "alias": "c",
               "on": [ { "left": "i.customerId", "right": "c.id" } ] } ],
  "select": [
    { "field": "c.name", "alias": "customer" },
    { "field": "i.amount", "aggregate": "sum", "alias": "revenue" }
  ],
  "where": { "logic": "and", "conditions": [
    { "field": "i.issuedOn", "operator": "between", "value": ["2026-01-01", "2026-03-31"] },
    { "logic": "or", "conditions": [
      { "field": "c.name", "operator": "startsWith", "value": "a", "caseInsensitive": true },
      { "field": "c.city", "operator": "isNull" } ] }
  ] },
  "groupBy": [ "c.name" ],
  "having": { "conditions": [ { "field": "i.amount", "aggregate": "sum", "operator": "gt", "value": 100 } ] },
  "orderBy": [ { "field": "revenue", "direction": "desc" } ],
  "pagination": { "mode": "offset", "page": 1, "pageSize": 20 },
  "includeTotalCount": true
}
```

and get back:

```json
{
  "data": [ { "customer": "Ada", "revenue": 150.00 }, { "customer": "Linus", "revenue": 75.50 } ],
  "page": { "pageSize": 20, "page": 1, "totalCount": 2, "totalPages": 1, "hasNextPage": false }
}
```

**Operators:** `eq`, `neq`, `gt`, `gte`, `lt`, `lte`, `in`, `notIn`, `between`, `contains`, `notContains`,
`startsWith`, `endsWith`, `isNull`, `isNotNull`. Conditions nest in `and`/`or` groups with optional `not`.
LIKE wildcards in values are escaped, so `contains: "50%"` means the literal text.
**Aggregates:** `count` (including `"*"`), `countDistinct`, `sum`, `avg`, `min`, `max`.
**Joins:** `inner`, `left`, `right`, `full` (not on MySQL).

**Pagination.** `offset` mode uses page numbers and can report total pages. `cursor` mode returns a `nextCursor`
token to pass back as `after`; it's stable and fast at any depth, handles ties and nulls, and needs the table to
have a key. Without an `orderBy`, rows are ordered by the key so pages never overlap.

**Errors** are collected, not just the first one, each with the path in the request:

```json
{
  "select[0].field": [ "Unknown field 'nmae'. Did you mean 'name'?" ],
  "where.conditions[0].value": [ "Expected a whole number, but got \"abc\"." ],
  "pagination.pageSize": [ "Must be between 1 and 100." ]
}
```

Suggestions only ever mention allowlisted names, so errors never reveal hidden columns.

To see the SQL without running it: `schema.Translate(request, SqlDialect.SqlServer).Data.Text`.

## More

### Async streaming (.NET 8+)

```csharp
await foreach (var u in connection.QueryUnbufferedAsync<User>($"SELECT * FROM Users"))
{
    ...
}
```

### Records and immutable types

If a type has no parameterless constructor, SqlWright uses the public constructor whose parameters all match result columns:

```csharp
public record Product(int Id, string Name, decimal Price);

var products = connection.Query<Product>($"SELECT Id, Name, Price FROM Products");
```

### Dynamic rows

```csharp
foreach (var row in connection.Query($"SELECT Id, Name FROM Users"))
{
    Console.WriteLine($"{row.Id}: {row.Name}");
}
```

Rows are `ExpandoObject`s, so they can also be cast to `IDictionary<string, object?>`.

### Multiple result sets

```csharp
using var multi = connection.QueryMultiple($"""
    SELECT * FROM Orders WHERE Id = {id};
    SELECT * FROM OrderLines WHERE OrderId = {id};
    """);

var order = multi.ReadSingle<Order>();
var lines = multi.Read<OrderLine>().ToList();
```

### Stored procedures and output parameters

```csharp
var p = new SqlParameters(new { Name = "Ada" });
p.Add("Id", dbType: DbType.Int32, direction: ParameterDirection.Output);

connection.Execute("CreateUser", p, commandType: CommandType.StoredProcedure);
int id = p.Get<int>("Id");
```

### Transactions

```csharp
connection.Open();
using var tx = connection.BeginTransaction();
connection.Execute($"UPDATE Accounts SET Balance = Balance - {amount} WHERE Id = {from}", tx);
connection.Execute($"UPDATE Accounts SET Balance = Balance + {amount} WHERE Id = {to}", tx);
tx.Commit();
```

## Settings

| Setting | Default | Description |
| --- | --- | --- |
| `MatchNamesWithUnderscores` | `false` | Ignore underscores when matching columns to members (`first_name` → `FirstName`). |
| `StrictMapping` | `false` | Throw when a result column matches no member. |
| `NamingStyle` | `AsIs` | How CRUD derives table/column names (`AsIs` or `SnakeCase`). |
| `TableNameResolver` | `null` | Custom table naming for CRUD, e.g. `t => t.Name + "s"`. |
| `DefaultDialect` | `null` | Dialect for unrecognised connection types. |
| `CommandTimeout` | `null` | Default command timeout in seconds. |
| `RegisterDialect<TConnection>(dialect)` | | Map a connection type to a dialect. |
| `ClearCache()` | | Drop cached mappers and metadata. |

All settings live on `SqlWrightSettings` and should be set once at startup.

## License

MIT
