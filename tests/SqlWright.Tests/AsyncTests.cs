namespace SqlWright.Tests;

public class AsyncTests : Database
{
    public AsyncTests() => SeedUsers();

    public class User
    {
        public long Id { get; set; }
        public string? Email { get; set; }
    }

    [Fact]
    public async Task Async_query_methods_map_rows()
    {
        var users = (await Connection.QueryAsync<User>("SELECT Id, Email FROM Users ORDER BY Id")).ToList();
        Assert.Equal(3, users.Count);

        Assert.Equal(2, (await Connection.QuerySingleAsync<User>("SELECT * FROM Users WHERE Id = @id", new { id = 2 })).Id);
        Assert.Equal(1, (await Connection.QueryFirstAsync<User>("SELECT * FROM Users ORDER BY Id")).Id);
        Assert.Null(await Connection.QueryFirstOrDefaultAsync<User>("SELECT * FROM Users WHERE Id = -1"));
        Assert.Null(await Connection.QuerySingleOrDefaultAsync<User>("SELECT * FROM Users WHERE Id = -1"));
        await Assert.ThrowsAsync<SqlWrightException>(() => Connection.QuerySingleAsync<User>("SELECT * FROM Users"));

        var dyn = (await Connection.QueryAsync("SELECT Id FROM Users WHERE Id = 3")).Single();
        Assert.Equal(3L, (long)dyn.Id);
        AssertClosed(Connection);
    }

    [Fact]
    public async Task Async_execute_and_scalar()
    {
        Assert.Equal(2, await Connection.ExecuteAsync("DELETE FROM Users WHERE Id IN @ids", new { ids = new[] { 1, 2 } }));
        Assert.Equal(1, await Connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM Users"));
        Assert.Equal(2, await Connection.ExecuteAsync(
            "INSERT INTO Users (Id, first_name) VALUES (@Id, @Name)",
            new[] { new { Id = 10, Name = "A" }, new { Id = 11, Name = "B" } }));
        AssertClosed(Connection);
    }

    [Fact]
    public async Task Async_query_multiple()
    {
        using var multi = await Connection.QueryMultipleAsync("SELECT COUNT(*) FROM Users; SELECT * FROM Users ORDER BY Id;");
        Assert.Equal(3, await multi.ReadSingleAsync<int>());
        Assert.Equal(3, (await multi.ReadAsync<User>()).Count());
        Assert.True(multi.IsConsumed);
    }

    [Fact]
    public async Task Unbuffered_async_streams_rows()
    {
        var ids = new List<long>();
        await foreach (var user in Connection.QueryUnbufferedAsync<User>("SELECT * FROM Users ORDER BY Id"))
            ids.Add(user.Id);

        Assert.Equal(new long[] { 1, 2, 3 }, ids);
        AssertClosed(Connection);
    }

    [Fact]
    public async Task Cancellation_is_observed()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Connection.QueryAsync<User>("SELECT * FROM Users", cancellationToken: cts.Token));
    }
}
