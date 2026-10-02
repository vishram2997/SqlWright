using SqlWright.Internal;
using SqlWright.Mapping;

namespace SqlWright.Tests;

public class CrudTests : Database
{
    [Table("Users")]
    public class User
    {
        public long Id { get; set; }
        [Column("first_name")] public string FirstName { get; set; } = "";
        public string? Email { get; set; }
        public int? Age { get; set; }
        public UserStatus Status { get; set; }
        public string Display => $"{FirstName} <{Email}>";
    }

    [Table("Users")]
    public record UserRecord(long Id, [property: Column("first_name")] string FirstName, string? Email);

    [Table("order_lines")]
    public class OrderLine
    {
        [Key] public int OrderId { get; set; }
        [Key] public int LineNo { get; set; }
        public string ProductName { get; set; } = "";
    }

    public class Note
    {
        public Guid Id { get; set; }
        public string Body { get; set; } = "";
        [Computed] public DateTime CreatedAt { get; set; }
    }

    public class NoKey
    {
        public string Name { get; set; } = "";
    }

    [Fact]
    public void Insert_writes_back_generated_key_and_get_reads_it()
    {
        var user = new User { FirstName = "Ada", Email = "ada@example.com", Age = 36, Status = UserStatus.Active };

        Assert.Equal(1, Connection.Insert(user));
        Assert.True(user.Id > 0);

        var loaded = Connection.Get<User>(user.Id);
        Assert.NotNull(loaded);
        Assert.Equal("Ada", loaded!.FirstName);
        Assert.Equal(UserStatus.Active, loaded.Status);
        Assert.Null(Connection.Get<User>(999));
        AssertClosed(Connection);
    }

    [Fact]
    public void Update_and_delete_by_entity_and_by_id()
    {
        var users = new[] { new User { FirstName = "A" }, new User { FirstName = "B" }, new User { FirstName = "C" } };
        Assert.Equal(3, Connection.InsertAll(users));
        Assert.Equal(3, users.Select(u => u.Id).Distinct().Count());

        users[0].FirstName = "A2";
        Assert.True(Connection.Update(users[0]));
        Assert.Equal("A2", Connection.Get<User>(users[0].Id)!.FirstName);

        Assert.True(Connection.Delete(users[1]));
        Assert.True(Connection.DeleteById<User>(users[2].Id));
        Assert.False(Connection.DeleteById<User>(users[2].Id));

        Assert.Equal(new[] { "A2" }, Connection.GetAll<User>().Select(u => u.FirstName));
    }

    [Fact]
    public void Records_work_with_crud()
    {
        var record = new UserRecord(0, "Grace", null);
        Connection.Insert(record);
        Assert.True(record.Id > 0);
        Assert.Equal(record, Connection.Get<UserRecord>(record.Id));
    }

    [Fact]
    public void Composite_keys_and_snake_case_naming()
    {
        SqlWrightSettings.NamingStyle = NamingStyle.SnakeCase;

        Connection.InsertAll(new[]
        {
            new OrderLine { OrderId = 1, LineNo = 1, ProductName = "Widget" },
            new OrderLine { OrderId = 1, LineNo = 2, ProductName = "Gadget" },
        });

        var line = Connection.Get<OrderLine>(new { OrderId = 1, LineNo = 2 });
        Assert.Equal("Gadget", line!.ProductName);

        line.ProductName = "Gizmo";
        Assert.True(Connection.Update(line));
        Assert.Equal("Gizmo", Connection.QuerySingle<string>("SELECT product_name FROM order_lines WHERE line_no = 2"));

        Assert.True(Connection.DeleteById<OrderLine>(new { OrderId = 1, LineNo = 1 }));
        Assert.Single(Connection.GetAll<OrderLine>());
    }

    [Fact]
    public void Explicit_keys_are_inserted_and_computed_columns_are_left_to_the_database()
    {
        SqlWrightSettings.TableNameResolver = type => type.Name + "s";
        var note = new Note { Id = Guid.NewGuid(), Body = "hello" };
        Connection.Insert(note);

        var loaded = Connection.Get<Note>(note.Id);
        Assert.Equal("hello", loaded!.Body);
        Assert.True(loaded.CreatedAt > DateTime.UtcNow.AddMinutes(-5));
    }

    [Fact]
    public async Task Async_crud()
    {
        var user = new User { FirstName = "Async" };
        await Connection.InsertAsync(user);
        Assert.Equal("Async", (await Connection.GetAsync<User>(user.Id))!.FirstName);

        user.Age = 30;
        Assert.True(await Connection.UpdateAsync(user));
        Assert.Equal(30, (await Connection.GetAllAsync<User>()).Single().Age);

        Assert.Equal(2, await Connection.InsertAllAsync(new[] { new User { FirstName = "X" }, new User { FirstName = "Y" } }));
        Assert.True(await Connection.DeleteAsync(user));
        Assert.True(await Connection.DeleteByIdAsync<User>(user.Id + 1));
        Assert.Single(await Connection.GetAllAsync<User>());
        AssertClosed(Connection);
    }

    [Fact]
    public void Missing_key_is_explained()
    {
        var ex = Assert.Throws<SqlWrightException>(() => Connection.Update(new NoKey()));
        Assert.Contains("Mark the key with [Key], or name it 'Id' or 'NoKeyId'", ex.Message);
    }

    [Fact]
    public void Missing_composite_key_member_is_explained()
    {
        var ex = Assert.Throws<SqlWrightException>(() => Connection.Get<OrderLine>(new { OrderId = 1 }));
        Assert.Contains("'LineNo'", ex.Message);
    }

    [Fact]
    public void Generates_dialect_specific_sql()
    {
        var info = EntityInfo.Get(typeof(User));

        Assert.Equal(
            "INSERT INTO [Users] ([first_name], [Email], [Age], [Status]) VALUES (@FirstName, @Email, @Age, @Status); SELECT CAST(SCOPE_IDENTITY() AS bigint)",
            info.For(SqlDialect.SqlServer).Insert);
        Assert.Equal(
            "INSERT INTO \"Users\" (\"first_name\", \"Email\", \"Age\", \"Status\") VALUES (@FirstName, @Email, @Age, @Status) RETURNING \"Id\"",
            info.For(SqlDialect.PostgreSql).Insert);
        Assert.Equal(
            "UPDATE `Users` SET `first_name` = @FirstName, `Email` = @Email, `Age` = @Age, `Status` = @Status WHERE `Id` = @Id",
            info.For(SqlDialect.MySql).Update);
        Assert.Equal(
            "SELECT [Id], [first_name], [Email], [Age], [Status] FROM [Users] WHERE [Id] = @Id",
            info.For(SqlDialect.SqlServer).SelectById);
    }

    [Theory]
    [InlineData("FirstName", "first_name")]
    [InlineData("UserID", "user_id")]
    [InlineData("HTMLParser", "html_parser")]
    [InlineData("Address2Line", "address2_line")]
    [InlineData("id", "id")]
    public void Snake_case_conversion(string input, string expected)
    {
        Assert.Equal(expected, EntityInfo.ToSnakeCase(input));
    }
}
