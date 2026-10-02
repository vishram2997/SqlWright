using System.Data;

namespace SqlWright.Tests;

public class ExecuteTests : Database
{
    public ExecuteTests() => SeedUsers();

    [Fact]
    public void Execute_returns_rows_affected_and_closes_connection()
    {
        var affected = Connection.Execute("UPDATE Users SET Age = Age + 1 WHERE Status = @status", new { status = UserStatus.Active });

        Assert.Equal(2, affected);
        AssertClosed(Connection);
    }

    [Fact]
    public void Execute_with_sequence_runs_once_per_item()
    {
        var affected = Connection.Execute("DELETE FROM Users WHERE Id = @Id", new[] { new { Id = 1 }, new { Id = 3 }, new { Id = 99 } });

        Assert.Equal(2, affected);
        Assert.Equal(1, Connection.ExecuteScalar<int>("SELECT COUNT(*) FROM Users"));
    }

    [Fact]
    public void ExecuteScalar_converts_and_handles_null()
    {
        Assert.Equal(3, Connection.ExecuteScalar<int>("SELECT COUNT(*) FROM Users"));
        Assert.Equal("Ada", Connection.ExecuteScalar<string>("SELECT first_name FROM Users WHERE Id = 1"));
        Assert.Null(Connection.ExecuteScalar<int?>("SELECT Age FROM Users WHERE Id = 2"));
        Assert.Null(Connection.ExecuteScalar("SELECT Age FROM Users WHERE Id = -1"));
    }

    [Theory]
    [InlineData("SELECT Id FROM Users WHERE Id IN @ids ORDER BY Id")]
    [InlineData("SELECT Id FROM Users WHERE Id IN (@ids) ORDER BY Id")]
    [InlineData("SELECT Id FROM Users WHERE Id IN ( @IDS ) ORDER BY Id")]
    public void List_parameters_expand_into_in_clauses(string sql)
    {
        Assert.Equal(new[] { 1, 3 }, Connection.Query<int>(sql, new { ids = new[] { 1, 3, 42 } }));
    }

    [Fact]
    public void Empty_list_parameter_matches_nothing()
    {
        Assert.Empty(Connection.Query<int>("SELECT Id FROM Users WHERE Id IN @ids", new { ids = Array.Empty<int>() }));
        Assert.Equal(3, Connection.ExecuteScalar<int>("SELECT COUNT(*) FROM Users WHERE Id NOT IN @ids", new { ids = new List<int>() }));
    }

    [Fact]
    public void List_expansion_does_not_touch_parameters_sharing_a_prefix()
    {
        var count = Connection.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM Users WHERE Id IN @id AND Age > @idMinAge",
            new { id = new[] { 1, 2, 3 }, idMinAge = 40 });
        Assert.Equal(1, count);
    }

    [Fact]
    public void Dictionary_and_SqlParameters_are_accepted()
    {
        var fromDict = Connection.QuerySingle<string>("SELECT first_name FROM Users WHERE Id = @Id",
            new Dictionary<string, object?> { ["@Id"] = 2 });
        Assert.Equal("Grace", fromDict);

        var p = new SqlParameters(new { Id = 1 }).Add("name", "Ada Lovelace", DbType.String, size: 50);
        Assert.Equal(1, Connection.Execute("UPDATE Users SET first_name = @name WHERE Id = @Id", p));
        Assert.Equal("Ada Lovelace", p.Get<string>("@name"));
        Assert.Equal(new[] { "Id", "name" }, p.ParameterNames);
        Assert.Equal("Ada Lovelace", Connection.QuerySingle<string>("SELECT first_name FROM Users WHERE Id = 1"));
    }

    [Fact]
    public void Null_values_are_sent_as_db_null()
    {
        Connection.Execute("UPDATE Users SET Email = @email WHERE Id = 1", new { email = (string?)null });
        Assert.Null(Connection.ExecuteScalar<string>("SELECT Email FROM Users WHERE Id = 1"));
    }

    [Fact]
    public void Transactions_are_honoured()
    {
        Connection.Open();
        using (var tx = Connection.BeginTransaction())
        {
            Connection.Execute("DELETE FROM Users", transaction: tx);
            Assert.Equal(0, Connection.ExecuteScalar<int>("SELECT COUNT(*) FROM Users", transaction: tx));
            tx.Rollback();
        }

        Assert.Equal(3, Connection.ExecuteScalar<int>("SELECT COUNT(*) FROM Users"));
        Assert.Equal(ConnectionState.Open, Connection.State); // caller opened it, so SqlWright leaves it open
    }
}
