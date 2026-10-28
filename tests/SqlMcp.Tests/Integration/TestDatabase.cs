using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using SqlMcp.Configuration;
using SqlMcp.Safety;

namespace SqlMcp.Tests.Integration;

/// <summary>
/// Creates a throwaway database for the integration tests and drops it afterwards. Point SQLMCP_TEST_SERVER at
/// any SQL Server (CI uses a container); without it the tests use LocalDB, and skip if neither is reachable.
/// </summary>
public sealed class TestDatabase : IAsyncLifetime
{
    private const string DefaultServer = @"Server=(localdb)\MSSQLLocalDB;Integrated Security=true;TrustServerCertificate=true";

    private static readonly string[] Seed =
    [
        "CREATE SCHEMA audit;",
        """
        CREATE TABLE dbo.Customers (
            Id int IDENTITY CONSTRAINT PK_Customers PRIMARY KEY,
            Name nvarchar(100) NOT NULL,
            Email nvarchar(200) NULL,
            PasswordHash varbinary(32) NULL,
            CreatedAt datetime2(0) NOT NULL CONSTRAINT DF_Customers_CreatedAt DEFAULT SYSUTCDATETIME());
        """,
        """
        CREATE TABLE dbo.Orders (
            Id int IDENTITY CONSTRAINT PK_Orders PRIMARY KEY,
            CustomerId int NOT NULL CONSTRAINT FK_Orders_Customers REFERENCES dbo.Customers (Id),
            Total decimal(10, 2) NOT NULL,
            Notes nvarchar(max) NULL);
        """,
        "CREATE INDEX IX_Orders_CustomerId ON dbo.Orders (CustomerId) INCLUDE (Total);",
        """
        CREATE TABLE audit.Events (
            Id int IDENTITY CONSTRAINT PK_Events PRIMARY KEY,
            CustomerId int NOT NULL CONSTRAINT FK_Events_Customers REFERENCES dbo.Customers (Id),
            Payload nvarchar(400) NULL);
        """,
        "EXEC sys.sp_addextendedproperty N'MS_Description', N'People who place orders.', N'SCHEMA', N'dbo', N'TABLE', N'Customers';",
        """
        INSERT dbo.Customers (Name, Email, PasswordHash) VALUES
            (N'Asha', N'asha@example.com', HASHBYTES('SHA2_256', N'one')),
            (N'Bilal', N'bilal@example.com', HASHBYTES('SHA2_256', N'two')),
            (N'Chen', NULL, HASHBYTES('SHA2_256', N'three'));
        """,
        """
        INSERT dbo.Orders (CustomerId, Total, Notes)
        SELECT 1 + n % 3, n * 10.5, REPLICATE(N'x', n * 40)
        FROM (SELECT TOP (50) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS n FROM sys.all_objects) AS numbers;
        """,
        "INSERT audit.Events (CustomerId, Payload) VALUES (1, N'login');",
    ];

    private readonly string _serverConnectionString =
        Environment.GetEnvironmentVariable("SQLMCP_TEST_SERVER") is { Length: > 0 } server ? server : DefaultServer;

    private readonly string _databaseName = $"SqlMcpTests_{Guid.NewGuid():N}"[..28];

    public bool Available { get; private set; }

    public string SkipReason { get; private set; } = string.Empty;

    public string ConnectionString { get; private set; } = string.Empty;

    public IOptions<SqlMcpOptions> CreateOptions(Action<SqlMcpOptions>? configure = null)
    {
        var options = new SqlMcpOptions { ConnectionString = ConnectionString, MaxCellLength = 100 };
        options.DeniedTables.Add("audit.*");
        configure?.Invoke(options);
        return Options.Create(options);
    }

    public static AccessPolicy CreatePolicy(IOptions<SqlMcpOptions> options) => new(options);

    public void SkipIfUnavailable() => Assert.SkipUnless(Available, SkipReason);

    public async ValueTask InitializeAsync()
    {
        try
        {
            await ExecuteAsync(_serverConnectionString, $"CREATE DATABASE [{_databaseName}];");
        }
        catch (SqlException ex) when (Environment.GetEnvironmentVariable("SQLMCP_TEST_SERVER") is not { Length: > 0 })
        {
            SkipReason = $"No SQL Server for integration tests ({ex.Message}). Set SQLMCP_TEST_SERVER or install LocalDB.";
            return;
        }

        ConnectionString = new SqlConnectionStringBuilder(_serverConnectionString) { InitialCatalog = _databaseName }.ConnectionString;
        foreach (var statement in Seed)
        {
            await ExecuteAsync(ConnectionString, statement);
        }

        Available = true;
    }

    public async ValueTask DisposeAsync()
    {
        if (!Available)
        {
            return;
        }

        SqlConnection.ClearAllPools();
        await ExecuteAsync(_serverConnectionString, $"ALTER DATABASE [{_databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_databaseName}];");
    }

    public async Task<int> ScalarAsync(string sql)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        return Convert.ToInt32(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}

[CollectionDefinition(Name)]
public sealed class DatabaseGroup : ICollectionFixture<TestDatabase>
{
    public const string Name = "Database";
}
