using System.Data;
using Microsoft.Data.Sqlite;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace SqlWright.Tests;

/// <summary>
/// A named, shared-cache in-memory SQLite database per test class. The keep-alive connection holds the
/// database open so tests can also exercise SqlWright's open-if-closed behaviour on a second connection.
/// </summary>
public abstract class Database : IDisposable
{
    private readonly string _connectionString = $"Data Source=sqlwright-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
    private readonly SqliteConnection _keepAlive;

    protected Database()
    {
        _keepAlive = new SqliteConnection(_connectionString);
        _keepAlive.Open();
        _keepAlive.Execute("""
            CREATE TABLE Users (
                Id INTEGER PRIMARY KEY,
                first_name TEXT NOT NULL,
                Email TEXT NULL,
                Age INTEGER NULL,
                Status INTEGER NOT NULL DEFAULT 0,
                ExternalId TEXT NULL,
                CreatedAt TEXT NULL
            );
            CREATE TABLE order_lines (
                order_id INTEGER NOT NULL,
                line_no INTEGER NOT NULL,
                product_name TEXT NOT NULL,
                PRIMARY KEY (order_id, line_no)
            );
            CREATE TABLE Notes (
                Id TEXT PRIMARY KEY,
                Body TEXT NOT NULL,
                CreatedAt TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
            );
            """);
        Connection = new SqliteConnection(_connectionString);
    }

    /// <summary>A closed connection; SqlWright opens and closes it per call.</summary>
    protected SqliteConnection Connection { get; }

    protected void SeedUsers()
    {
        Connection.Execute(
            "INSERT INTO Users (Id, first_name, Email, Age, Status) VALUES (@Id, @FirstName, @Email, @Age, @Status)",
            new[]
            {
                new { Id = 1, FirstName = "Ada", Email = (string?)"ada@example.com", Age = (int?)36, Status = UserStatus.Active },
                new { Id = 2, FirstName = "Grace", Email = (string?)null, Age = (int?)null, Status = UserStatus.Disabled },
                new { Id = 3, FirstName = "Linus", Email = (string?)"linus@example.com", Age = (int?)54, Status = UserStatus.Active },
            });
    }

    public void Dispose()
    {
        Connection.Dispose();
        _keepAlive.Dispose();
        SqlWrightSettings.MatchNamesWithUnderscores = false;
        SqlWrightSettings.StrictMapping = false;
        SqlWrightSettings.NamingStyle = NamingStyle.AsIs;
        SqlWrightSettings.TableNameResolver = null;
        GC.SuppressFinalize(this);
    }

    protected static void AssertClosed(IDbConnection connection) => Assert.Equal(ConnectionState.Closed, connection.State);
}

public enum UserStatus
{
    Pending = 0,
    Active = 1,
    Disabled = 2,
}
