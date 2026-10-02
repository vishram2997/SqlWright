using System.Data;

namespace SqlWright.SqlServer.Tests;

public class QueryTests : SqlServerTest
{
    public QueryTests(SqlServerFixture fixture) : base(fixture)
    {
        Connection.Execute(
            "INSERT INTO dbo.Users (UserName, Email, Age, Status, Balance) VALUES (@UserName, @Email, @Age, @Status, @Balance)",
            new[]
            {
                new { UserName = "ada", Email = (string?)"ada@example.com", Age = (int?)36, Status = UserStatus.Active, Balance = 10.50m },
                new { UserName = "grace", Email = (string?)null, Age = (int?)null, Status = UserStatus.Disabled, Balance = 0m },
                new { UserName = "linus", Email = (string?)"linus@example.com", Age = (int?)54, Status = UserStatus.Active, Balance = 99.99m },
            });
    }

    [Fact]
    public void Interpolated_query_with_enum_and_null_parameters()
    {
        var status = UserStatus.Active;
        int? minAge = 40;

        var users = Connection.Query<User>($"SELECT * FROM dbo.Users WHERE Status = {status} AND Age > {minAge}").ToList();

        var user = Assert.Single(users);
        Assert.Equal("linus", user.UserName);
        Assert.Equal(99.99m, user.Balance);
        Assert.Equal(8, user.RowVer!.Length);
        Assert.True(user.CreatedAt > DateTime.UtcNow.AddMinutes(-5));
        Assert.Equal(ConnectionState.Closed, Connection.State);
    }

    [Fact]
    public void Injection_attempt_is_treated_as_a_value()
    {
        var name = "ada'; DROP TABLE dbo.Users; --";
        Assert.Empty(Connection.Query<User>($"SELECT * FROM dbo.Users WHERE UserName = {name}"));
        Assert.Equal(3, Connection.ExecuteScalar<int>($"SELECT COUNT(*) FROM dbo.Users"));
    }

    [Fact]
    public void String_constant_holes_are_folded_by_the_compiler_unless_target_typed()
    {
        // C# turns an interpolated string whose holes are all string constants into a plain constant string,
        // which binds to the string overload. Target-typing to Sql keeps them as parameters.
        Sql query = $"SELECT COUNT(*) FROM dbo.Users WHERE UserName = {"ada"}";
        Assert.Equal("SELECT COUNT(*) FROM dbo.Users WHERE UserName = @p0", query.Text);
        Assert.Equal(1, Connection.ExecuteScalar<int>(query));
    }

    [Fact]
    public void Large_in_list_expands()
    {
        var ids = Enumerable.Range(-1500, 1500).Concat(Connection.Query<int>("SELECT Id FROM dbo.Users")).ToArray();
        Assert.Equal(3, Connection.ExecuteScalar<int>($"SELECT COUNT(*) FROM dbo.Users WHERE Id IN {ids}"));
        Assert.Equal(0, Connection.ExecuteScalar<int>($"SELECT COUNT(*) FROM dbo.Users WHERE Id IN {Array.Empty<int>()}"));
    }

    [Fact]
    public void Composed_query_with_dynamic_filters()
    {
        string? search = "a";
        decimal? minBalance = 5m;

        var filters = new List<Sql>();
        if (search != null) filters.Add($"UserName LIKE {"%" + search + "%"}");
        if (minBalance != null) filters.Add($"Balance >= {minBalance}");

        Sql query = $"SELECT UserName FROM dbo.Users WHERE {Sql.Join(" AND ", filters)} ORDER BY UserName";

        Assert.Equal(new[] { "ada" }, Connection.Query<string>(query));
    }

    [Fact]
    public void Dapper_style_parameter_object_still_works()
    {
        var user = Connection.QuerySingle<User>("SELECT * FROM dbo.Users WHERE UserName = @name", new { name = "grace" });
        Assert.Null(user.Email);
        Assert.Null(user.Age);
        Assert.Equal(UserStatus.Disabled, user.Status);
    }

    [Fact]
    public void Dynamic_rows()
    {
        var name = "ada";
        var row = Connection.QuerySingle<dynamic>($"SELECT UserName, Balance FROM dbo.Users WHERE UserName = {name}");
        Assert.Equal("ada", (string)row.UserName);
        Assert.Equal(10.50m, (decimal)row.Balance);
    }

    [Fact]
    public void Multiple_result_sets()
    {
        var name = "ada";
        using var multi = Connection.QueryMultiple($"""
            SELECT COUNT(*) FROM dbo.Users;
            SELECT * FROM dbo.Users WHERE UserName = {name};
            SELECT UserName FROM dbo.Users ORDER BY UserName;
            """);

        Assert.Equal(3, multi.ReadSingle<int>());
        Assert.Equal(36, multi.ReadSingle<User>().Age);
        Assert.Equal(new[] { "ada", "grace", "linus" }, multi.Read<string>());
        Assert.True(multi.IsConsumed);
    }

    [Fact]
    public async Task Async_and_streaming()
    {
        var users = await Connection.QueryAsync<User>($"SELECT * FROM dbo.Users ORDER BY UserName");
        Assert.Equal(3, users.Count());

        var names = new List<string>();
        await foreach (var name in Connection.QueryUnbufferedAsync<string>($"SELECT UserName FROM dbo.Users ORDER BY UserName"))
            names.Add(name);
        Assert.Equal(new[] { "ada", "grace", "linus" }, names);
        Assert.Equal(ConnectionState.Closed, Connection.State);
    }

    [Fact]
    public async Task Cancellation_stops_a_running_query()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var started = DateTime.UtcNow;

        await Assert.ThrowsAnyAsync<Exception>(() =>
            Connection.QueryAsync<int>("WAITFOR DELAY '00:00:10'; SELECT 1", cancellationToken: cts.Token));

        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5));
    }
}
