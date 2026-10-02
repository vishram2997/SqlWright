namespace SqlWright.SqlServer.Tests;

public class CrudTests : SqlServerTest
{
    public CrudTests(SqlServerFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public void Insert_returns_identity_even_with_a_trigger_inserting_elsewhere()
    {
        var user = new User { UserName = "ada", Email = "ada@example.com", Balance = 12.34m, Status = UserStatus.Active };

        Connection.Insert(user);

        // The trigger inserted into UserAudit (identity seeded at 1000); SCOPE_IDENTITY must ignore that.
        Assert.Equal(Connection.ExecuteScalar<int>($"SELECT MAX(Id) FROM dbo.Users"), user.Id);
        Assert.True(user.Id < 1000);
        Assert.Equal(user.Id, Connection.ExecuteScalar<int>($"SELECT UserId FROM dbo.UserAudit"));
    }

    [Fact]
    public void Get_update_delete_round_trip_with_computed_columns()
    {
        var user = new User { UserName = "grace", Balance = 1m };
        Connection.Insert(user);

        var loaded = Connection.Get<User>(user.Id)!;
        Assert.Equal("grace", loaded.UserName);
        Assert.NotNull(loaded.RowVer);          // ROWVERSION produced by the server
        var originalVersion = loaded.RowVer;

        loaded.Email = "grace@example.com";
        loaded.Age = 85;
        Assert.True(Connection.Update(loaded));

        var updated = Connection.Get<User>(user.Id)!;
        Assert.Equal("grace@example.com", updated.Email);
        Assert.Equal(85, updated.Age);
        Assert.NotEqual(originalVersion, updated.RowVer);

        Assert.True(Connection.Delete(updated));
        Assert.Null(Connection.Get<User>(user.Id));
        Assert.False(Connection.DeleteById<User>(user.Id));
    }

    [Fact]
    public void Schema_qualified_record_with_guid_key_and_datetimeoffset()
    {
        var order = new Order(Guid.NewGuid(), "Contoso", 250.75m, new DateTimeOffset(2026, 10, 1, 9, 30, 0, TimeSpan.FromHours(5.5)));

        Connection.Insert(order);

        Assert.Equal(order, Connection.Get<Order>(order.Id));
        Assert.Single(Connection.GetAll<Order>());
        Assert.True(Connection.DeleteById<Order>(order.Id));
    }

    [Fact]
    public void Composite_keys_with_snake_case_columns()
    {
        SqlWrightSettings.NamingStyle = NamingStyle.SnakeCase;

        Assert.Equal(3, Connection.InsertAll(new[]
        {
            new OrderLine { OrderId = 7, LineNo = 1, ProductName = "Widget", Quantity = 2 },
            new OrderLine { OrderId = 7, LineNo = 2, ProductName = "Gadget", Quantity = 1 },
            new OrderLine { OrderId = 8, LineNo = 1, ProductName = "Gizmo", Quantity = 5 },
        }));

        var line = Connection.Get<OrderLine>(new { OrderId = 7, LineNo = 2 })!;
        Assert.Equal("Gadget", line.ProductName);

        line.Quantity = 10;
        Assert.True(Connection.Update(line));
        Assert.Equal(10, Connection.ExecuteScalar<int>($"SELECT quantity FROM dbo.order_lines WHERE order_id = {7} AND line_no = {2}"));

        Assert.True(Connection.DeleteById<OrderLine>(new { OrderId = 8, LineNo = 1 }));
        Assert.Equal(2, Connection.GetAll<OrderLine>().Count());
    }

    [Fact]
    public async Task Async_crud()
    {
        var users = new[] { new User { UserName = "a" }, new User { UserName = "b" } };
        Assert.Equal(2, await Connection.InsertAllAsync(users));
        Assert.All(users, u => Assert.True(u.Id > 0));

        var a = (await Connection.GetAsync<User>(users[0].Id))!;
        a.Balance = 5m;
        Assert.True(await Connection.UpdateAsync(a));
        Assert.Equal(5m, (await Connection.GetAsync<User>(a.Id))!.Balance);

        Assert.True(await Connection.DeleteAsync(a));
        Assert.True(await Connection.DeleteByIdAsync<User>(users[1].Id));
        Assert.Empty(await Connection.GetAllAsync<User>());
    }

    [Fact]
    public void Transaction_rollback_discards_crud_changes()
    {
        Connection.Open();
        using (var tx = Connection.BeginTransaction())
        {
            var user = new User { UserName = "temp" };
            Connection.Insert(user, tx);
            Assert.NotNull(Connection.Get<User>(user.Id, tx));
            tx.Rollback();
        }

        Assert.Equal(0, Connection.ExecuteScalar<int>($"SELECT COUNT(*) FROM dbo.Users"));
    }
}
