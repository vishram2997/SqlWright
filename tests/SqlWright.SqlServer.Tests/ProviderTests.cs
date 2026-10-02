using System.Data;
using Microsoft.Data.SqlClient;

namespace SqlWright.SqlServer.Tests;

public class ProviderTests : SqlServerTest
{
    public ProviderTests(SqlServerFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public void Stored_procedure_with_output_and_return_value()
    {
        var p = new SqlParameters(new { UserName = "ada", Email = "ada@example.com" })
            .Add("Id", dbType: DbType.Int32, direction: ParameterDirection.Output)
            .Add("ReturnValue", dbType: DbType.Int32, direction: ParameterDirection.ReturnValue);

        Connection.Execute("dbo.CreateUser", p, commandType: CommandType.StoredProcedure);

        var id = p.Get<int>("Id");
        Assert.True(id > 0);
        Assert.Equal(42, p.Get<int>("ReturnValue"));
        Assert.Equal("ada", Connection.Get<User>(id)!.UserName);
    }

    [Fact]
    public void Sql_server_types_round_trip()
    {
        var payload = new byte[] { 1, 2, 3, 255 };
        var reference = Guid.NewGuid();
        var stamp = new DateTime(2026, 1, 2, 3, 4, 5);

        Connection.Execute($"""
            INSERT INTO dbo.TypeSamples (Flag, Small, Big, Ratio, Amount, Day, Clock, Stamp, Payload, Code, Note, Ref)
            VALUES ({true}, {(short)7}, {long.MaxValue}, {0.25}, {12.3456m}, {new DateTime(2026, 10, 1)}, {new TimeSpan(13, 45, 0)},
                    {stamp}, {payload}, {"ABC"}, {"नमस्ते 👋"}, {reference})
            """);
        Connection.Execute($"INSERT INTO dbo.TypeSamples (Flag) VALUES ({false})");

        var rows = Connection.Query<TypeSample>($"SELECT * FROM dbo.TypeSamples ORDER BY Id").ToList();

        var full = rows[0];
        Assert.True(full.Flag);
        Assert.Equal((short)7, full.Small);
        Assert.Equal(long.MaxValue, full.Big);
        Assert.Equal(0.25, full.Ratio);
        Assert.Equal(12.3456m, full.Amount);
        Assert.Equal(new DateOnly(2026, 10, 1), full.Day);   // DATE -> DateTime -> DateOnly
        Assert.Equal(new TimeOnly(13, 45), full.Clock);      // TIME -> TimeSpan -> TimeOnly
        Assert.Equal(stamp, full.Stamp);
        Assert.Equal(payload, full.Payload);
        Assert.Equal("ABC", full.Code);
        Assert.Equal("नमस्ते 👋", full.Note);
        Assert.Equal(reference, full.Ref);

        var empty = rows[1];
        Assert.False(empty.Flag);
        Assert.Null(empty.Small);
        Assert.Null(empty.Day);
        Assert.Null(empty.Payload);
        Assert.Null(empty.Ref);
    }

    [Fact]
    public void Database_errors_pass_through_unwrapped()
    {
        Connection.Insert(new User { UserName = "dup" });

        var ex = Assert.Throws<SqlException>(() => Connection.Insert(new User { UserName = "dup" }));
        Assert.Equal(2627, ex.Number); // unique constraint violation
    }

    [Fact]
    public void Mapping_errors_explain_the_problem()
    {
        Connection.Insert(new User { UserName = "ada" });

        var ex = Assert.Throws<MappingException>(() =>
            Connection.Query<User>($"SELECT Id, UserName AS Age FROM dbo.Users").ToList());

        Assert.Equal("Age", ex.ColumnName);
        Assert.Equal("User.Age", ex.MemberName);
        Assert.Contains("'ada'", ex.Message);
    }

    [Fact]
    public void Strict_mapping_catches_typos()
    {
        Connection.Insert(new User { UserName = "ada" });
        SqlWrightSettings.StrictMapping = true;

        var ex = Assert.Throws<MappingException>(() =>
            Connection.Query<User>($"SELECT Id, UserName AS UsreName FROM dbo.Users").ToList());

        Assert.Contains("Did you mean 'UserName'?", ex.Message);
    }

    [Fact]
    public void Dialect_is_detected_from_SqlConnection()
    {
        Sql query = $"SELECT {1}";
        Assert.Equal(1, Connection.ExecuteScalar<int>(query));
        Assert.Equal("[dbo].[Users]", SqlDialect.SqlServer.QuoteTableName("Users", "dbo"));
    }
}
