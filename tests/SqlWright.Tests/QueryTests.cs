using SqlWright.Mapping;

namespace SqlWright.Tests;

public class QueryTests : Database
{
    public QueryTests() => SeedUsers();

    public class User
    {
        public int Id { get; set; }
        [Column("first_name")] public string FirstName { get; set; } = "";
        public string? Email { get; set; }
        public int? Age { get; set; }
        public UserStatus Status { get; set; }
        [NotMapped] public string Computed { get; set; } = "untouched";
    }

    public class SnakeUser
    {
        public int Id { get; set; }
        public string FirstName { get; set; } = "";
    }

    public record UserRecord(long Id, string FirstName, int? Age);

    public struct UserStruct
    {
        public int Id;
        public string? Email;
    }

    [Fact]
    public void Query_maps_rows_to_class_with_attributes_nullables_and_enums()
    {
        var users = Connection.Query<User>("SELECT * FROM Users ORDER BY Id").ToList();

        Assert.Equal(3, users.Count);
        Assert.Equal("Ada", users[0].FirstName);
        Assert.Equal(36, users[0].Age);
        Assert.Equal(UserStatus.Active, users[0].Status);
        Assert.Null(users[1].Email);
        Assert.Null(users[1].Age);
        Assert.Equal(UserStatus.Disabled, users[1].Status);
        Assert.All(users, u => Assert.Equal("untouched", u.Computed));
        AssertClosed(Connection);
    }

    [Fact]
    public void Query_matches_underscored_columns_when_enabled()
    {
        Assert.Equal("", Connection.QueryFirst<SnakeUser>("SELECT Id, first_name FROM Users WHERE Id = 1").FirstName);

        SqlWrightSettings.MatchNamesWithUnderscores = true;
        Assert.Equal("Ada", Connection.QueryFirst<SnakeUser>("SELECT Id, first_name FROM Users WHERE Id = 1").FirstName);
    }

    [Fact]
    public void Query_maps_positional_records_through_constructor()
    {
        var user = Connection.QuerySingle<UserRecord>("SELECT Id, first_name AS FirstName, Age FROM Users WHERE Id = 2");
        Assert.Equal(new UserRecord(2, "Grace", null), user);
    }

    [Fact]
    public void Query_maps_structs_and_fields()
    {
        var user = Connection.QueryFirst<UserStruct>("SELECT Id, Email FROM Users WHERE Id = 1");
        Assert.Equal(1, user.Id);
        Assert.Equal("ada@example.com", user.Email);
    }

    [Fact]
    public void Query_maps_single_column_to_scalars()
    {
        Assert.Equal(new[] { 1L, 2L, 3L }, Connection.Query<long>("SELECT Id FROM Users ORDER BY Id"));
        Assert.Equal(new int?[] { 36, null, 54 }, Connection.Query<int?>("SELECT Age FROM Users ORDER BY Id"));
        Assert.Equal(UserStatus.Disabled, Connection.QuerySingle<UserStatus>("SELECT Status FROM Users WHERE Id = 2"));
    }

    [Fact]
    public void Query_returns_dynamic_rows()
    {
        var row = Connection.Query("SELECT Id, first_name, Email FROM Users WHERE Id = 2").Single();
        Assert.Equal(2L, (long)row.Id);
        Assert.Equal("Grace", (string)row.first_name);
        Assert.Null(row.Email);

        var dict = (IDictionary<string, object?>)row;
        Assert.Equal(new[] { "Id", "first_name", "Email" }, dict.Keys);
    }

    [Fact]
    public void Query_unbuffered_streams_and_closes_connection_when_done()
    {
        var rows = Connection.Query<User>("SELECT * FROM Users ORDER BY Id", buffered: false);
        AssertClosed(Connection); // nothing has run yet

        using (var e = rows.GetEnumerator())
        {
            Assert.True(e.MoveNext());
            Assert.Equal(System.Data.ConnectionState.Open, Connection.State);
        }
        AssertClosed(Connection);
    }

    [Fact]
    public void First_and_single_variants_enforce_row_counts()
    {
        const string none = "SELECT * FROM Users WHERE Id = -1";
        const string many = "SELECT * FROM Users";

        Assert.Throws<SqlWrightException>(() => Connection.QueryFirst<User>(none));
        Assert.Null(Connection.QueryFirstOrDefault<User>(none));
        Assert.Equal(1, Connection.QueryFirst<User>(many + " ORDER BY Id").Id);

        Assert.Throws<SqlWrightException>(() => Connection.QuerySingle<User>(none));
        Assert.Throws<SqlWrightException>(() => Connection.QuerySingle<User>(many));
        Assert.Throws<SqlWrightException>(() => Connection.QuerySingleOrDefault<User>(many));
        Assert.Null(Connection.QuerySingleOrDefault<User>(none));
        Assert.Equal(0, Connection.QuerySingleOrDefault<int>("SELECT Id FROM Users WHERE Id = -1"));
    }

    [Fact]
    public void QueryMultiple_reads_result_sets_in_order()
    {
        using var multi = Connection.QueryMultiple("""
            SELECT COUNT(*) FROM Users;
            SELECT * FROM Users WHERE Id = @id;
            SELECT Id FROM Users ORDER BY Id;
            """, new { id = 3 });

        Assert.Equal(3, multi.ReadSingle<int>());
        Assert.Equal("Linus", multi.ReadFirst<User>().FirstName);
        Assert.Equal(new[] { 1, 2, 3 }, multi.Read<int>());
        Assert.True(multi.IsConsumed);
        Assert.Throws<InvalidOperationException>(() => multi.Read<int>());
    }

    [Fact]
    public void QueryMultiple_closes_connection_on_dispose()
    {
        var multi = Connection.QueryMultiple("SELECT 1; SELECT 2;");
        Assert.Equal(System.Data.ConnectionState.Open, Connection.State);
        multi.Dispose();
        AssertClosed(Connection);
    }

    [Fact]
    public void Text_values_convert_to_guid_and_datetime()
    {
        var id = Guid.NewGuid();
        var created = new DateTime(2024, 5, 6, 7, 8, 9, DateTimeKind.Utc);
        Connection.Execute("UPDATE Users SET ExternalId = @id, CreatedAt = @created WHERE Id = 1", new { id, created });

        Assert.Equal(id, Connection.QuerySingle<Guid>("SELECT ExternalId FROM Users WHERE Id = 1"));
        // SQLite stores no zone, so compare ticks only (DateTime equality ignores Kind).
        Assert.Equal(created, Connection.QuerySingle<DateTime>("SELECT CreatedAt FROM Users WHERE Id = 1"));
    }
}
