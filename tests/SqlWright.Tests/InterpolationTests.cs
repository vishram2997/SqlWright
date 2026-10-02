namespace SqlWright.Tests;

public class InterpolationTests : Database
{
    public InterpolationTests() => SeedUsers();

    [Fact]
    public void Interpolated_values_become_parameters()
    {
        var minAge = 40;
        var status = UserStatus.Active;

        var names = Connection.Query<string>($"SELECT first_name FROM Users WHERE Age > {minAge} AND Status = {status}");

        Assert.Equal(new[] { "Linus" }, names);
    }

    [Fact]
    public void Injection_attempts_are_just_values()
    {
        var name = "x' OR '1'='1";

        Assert.Null(Connection.QuerySingleOrDefault<string>($"SELECT first_name FROM Users WHERE first_name = {name}"));
        Assert.Equal(3, Connection.ExecuteScalar<int>("SELECT COUNT(*) FROM Users"));
    }

    [Fact]
    public void Lists_and_nulls_work_in_interpolation()
    {
        var ids = new[] { 1, 3 };
        Assert.Equal(new[] { 1, 3 }, Connection.Query<int>($"SELECT Id FROM Users WHERE Id IN {ids} ORDER BY Id"));

        string? email = null;
        Assert.Equal(1, Connection.Execute($"UPDATE Users SET Email = {email} WHERE Id = {1}"));
        Assert.Null(Connection.ExecuteScalar<string>($"SELECT Email FROM Users WHERE Id = {1}"));
    }

    [Fact]
    public void Fragments_compose_with_their_own_parameters()
    {
        var conditions = new List<Sql> { $"Status = {UserStatus.Active}" };
        int? minAge = 50;
        if (minAge != null) conditions.Add($"Age >= {minAge}");

        var where = Sql.Join(" AND ", conditions);
        Sql query = $"SELECT first_name FROM Users WHERE {where}";
        query.Append($" ORDER BY {"Id":raw}");

        Assert.Equal("SELECT first_name FROM Users WHERE Status = @p0 AND Age >= @p1 ORDER BY Id", query.Text);
        Assert.Equal(new object?[] { UserStatus.Active, 50 }, query.Values);
        Assert.Equal(new[] { "Linus" }, Connection.Query<string>(query));
    }

    [Fact]
    public void Raw_format_inserts_identifiers()
    {
        var table = "Users";
        Assert.Equal(3, Connection.ExecuteScalar<int>($"SELECT COUNT(*) FROM {table:raw}"));
    }

    [Fact]
    public void Unsupported_format_is_rejected_with_explanation()
    {
        var ex = Assert.Throws<FormatException>(() => { Sql _ = $"SELECT {DateTime.Now:yyyy}"; });
        Assert.Contains(":raw", ex.Message);
    }

    [Fact]
    public async Task Async_interpolated_queries()
    {
        var id = 2;
        Assert.Equal("Grace", await Connection.QuerySingleAsync<string>($"SELECT first_name FROM Users WHERE Id = {id}"));
        Assert.Equal(1, await Connection.ExecuteAsync($"DELETE FROM Users WHERE Id = {id}"));

        var remaining = new List<long>();
        await foreach (var row in Connection.QueryUnbufferedAsync<long>($"SELECT Id FROM Users WHERE Id > {0} ORDER BY Id"))
            remaining.Add(row);
        Assert.Equal(new long[] { 1, 3 }, remaining);
    }

    [Fact]
    public void Plain_strings_still_use_the_parameter_object_overload()
    {
        const string sql = "SELECT first_name FROM Users WHERE Id = @id";
        Assert.Equal("Ada", Connection.QuerySingle<string>(sql, new { id = 1 }));
    }
}
