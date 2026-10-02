using Microsoft.Data.SqlClient;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace SqlWright.SqlServer.Tests;

/// <summary>
/// Creates the SqlWrightTests database (if missing) and rebuilds its schema once per test run.
/// Override the connection with the SQLWRIGHT_SQLSERVER environment variable.
/// </summary>
public sealed class SqlServerFixture
{
    private const string DefaultConnectionString =
        "Server=localhost;Database=SqlWrightTests;Integrated Security=true;TrustServerCertificate=true";

    public SqlServerFixture()
    {
        ConnectionString = Environment.GetEnvironmentVariable("SQLWRIGHT_SQLSERVER") ?? DefaultConnectionString;

        var builder = new SqlConnectionStringBuilder(ConnectionString);
        var database = builder.InitialCatalog;
        builder.InitialCatalog = "master";
        using (var master = new SqlConnection(builder.ConnectionString))
        {
            // CREATE DATABASE can't take a parameter, so build it server-side with QUOTENAME.
            master.Execute($"""
                IF DB_ID({database}) IS NULL
                BEGIN
                    DECLARE @sql nvarchar(max) = N'CREATE DATABASE ' + QUOTENAME({database});
                    EXEC (@sql);
                END
                """);
        }

        using var db = new SqlConnection(ConnectionString);
        foreach (var batch in Schema) db.Execute(batch);
    }

    public string ConnectionString { get; }

    public SqlConnection CreateConnection() => new(ConnectionString);

    public void Reset()
    {
        using var db = CreateConnection();
        db.Execute("""
            DELETE FROM dbo.UserAudit;
            DELETE FROM dbo.Users;
            DELETE FROM sales.Orders;
            DELETE FROM dbo.order_lines;
            DELETE FROM dbo.TypeSamples;
            DELETE FROM dbo.DqInvoices;
            DELETE FROM dbo.DqCustomers;
            """);
    }

    private static readonly string[] Schema =
    {
        """
        DROP PROCEDURE IF EXISTS dbo.CreateUser;
        DROP TABLE IF EXISTS dbo.UserAudit;
        DROP TABLE IF EXISTS dbo.Users;
        DROP TABLE IF EXISTS sales.Orders;
        DROP TABLE IF EXISTS dbo.order_lines;
        DROP TABLE IF EXISTS dbo.TypeSamples;
        DROP TABLE IF EXISTS dbo.DqInvoices;
        DROP TABLE IF EXISTS dbo.DqCustomers;
        IF SCHEMA_ID('sales') IS NULL EXEC ('CREATE SCHEMA sales');
        """,
        """
        CREATE TABLE dbo.Users (
            Id INT IDENTITY(1,1) PRIMARY KEY,
            UserName NVARCHAR(100) NOT NULL CONSTRAINT UQ_Users_UserName UNIQUE,
            Email NVARCHAR(200) NULL,
            Age INT NULL,
            Status TINYINT NOT NULL DEFAULT 0,
            Balance DECIMAL(18,2) NOT NULL DEFAULT 0,
            CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
            RowVer ROWVERSION
        );

        -- Identity seeded at 1000 so @@IDENTITY (which a trigger hijacks) would differ from SCOPE_IDENTITY().
        CREATE TABLE dbo.UserAudit (
            Id INT IDENTITY(1000,1) PRIMARY KEY,
            UserId INT NOT NULL,
            Action NVARCHAR(20) NOT NULL
        );

        CREATE TABLE sales.Orders (
            Id UNIQUEIDENTIFIER PRIMARY KEY,
            CustomerName NVARCHAR(100) NOT NULL,
            Total DECIMAL(18,2) NOT NULL,
            PlacedAt DATETIMEOFFSET NOT NULL
        );

        CREATE TABLE dbo.order_lines (
            order_id INT NOT NULL,
            line_no INT NOT NULL,
            product_name NVARCHAR(100) NOT NULL,
            quantity INT NOT NULL,
            PRIMARY KEY (order_id, line_no)
        );

        CREATE TABLE dbo.TypeSamples (
            Id INT IDENTITY PRIMARY KEY,
            Flag BIT NOT NULL,
            Small SMALLINT NULL,
            Big BIGINT NULL,
            Ratio FLOAT NULL,
            Amount MONEY NULL,
            Day DATE NULL,
            Clock TIME NULL,
            Stamp DATETIME NULL,
            Payload VARBINARY(MAX) NULL,
            Code CHAR(3) NULL,
            Note NVARCHAR(MAX) NULL,
            Ref UNIQUEIDENTIFIER NULL
        );

        CREATE TABLE dbo.DqCustomers (
            Id INT IDENTITY(1,1) PRIMARY KEY,
            Name NVARCHAR(100) NOT NULL,
            City NVARCHAR(100) NULL,
            TenantId INT NOT NULL,
            CreatedAt DATETIME2 NOT NULL
        );

        CREATE TABLE dbo.DqInvoices (
            Id INT IDENTITY(1,1) PRIMARY KEY,
            CustomerId INT NOT NULL REFERENCES dbo.DqCustomers (Id),
            Amount DECIMAL(18,2) NOT NULL,
            Status NVARCHAR(20) NOT NULL,
            IssuedOn DATE NOT NULL,
            TenantId INT NOT NULL
        );
        """,
        """
        CREATE TRIGGER dbo.TR_Users_Insert ON dbo.Users AFTER INSERT AS
            INSERT INTO dbo.UserAudit (UserId, Action) SELECT Id, 'insert' FROM inserted;
        """,
        """
        CREATE PROCEDURE dbo.CreateUser
            @UserName NVARCHAR(100),
            @Email NVARCHAR(200),
            @Id INT OUTPUT
        AS
        BEGIN
            SET NOCOUNT ON;
            INSERT INTO dbo.Users (UserName, Email) VALUES (@UserName, @Email);
            SET @Id = SCOPE_IDENTITY();
            RETURN 42;
        END
        """,
    };
}

[CollectionDefinition("SqlServer")]
public sealed class SqlServerCollection : ICollectionFixture<SqlServerFixture>
{
}

/// <summary>Base class: resets data before each test and provides a closed connection.</summary>
[Collection("SqlServer")]
public abstract class SqlServerTest : IDisposable
{
    protected SqlServerTest(SqlServerFixture fixture)
    {
        Fixture = fixture;
        fixture.Reset();
        Connection = fixture.CreateConnection();
    }

    protected SqlServerFixture Fixture { get; }

    protected SqlConnection Connection { get; }

    public void Dispose()
    {
        Connection.Dispose();
        SqlWrightSettings.NamingStyle = NamingStyle.AsIs;
        SqlWrightSettings.StrictMapping = false;
        GC.SuppressFinalize(this);
    }
}
