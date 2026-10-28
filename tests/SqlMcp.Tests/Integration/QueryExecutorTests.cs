using Microsoft.Data.SqlClient;
using SqlMcp.Data;

namespace SqlMcp.Tests.Integration;

[Collection(DatabaseGroup.Name)]
public class QueryExecutorTests(TestDatabase db)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private QueryExecutor CreateExecutor(Action<SqlMcp.Configuration.SqlMcpOptions>? configure = null)
    {
        var options = db.CreateOptions(configure);
        return new QueryExecutor(options, TestDatabase.CreatePolicy(options));
    }

    [Fact]
    public async Task Returns_columns_and_rows()
    {
        db.SkipIfUnavailable();

        var result = await CreateExecutor().ExecuteAsync("SELECT Id, Name, Email FROM dbo.Customers ORDER BY Id", 100, Ct);

        Assert.Equal(["Id", "Name", "Email"], result.Columns.Select(c => c.Name));
        Assert.Equal("nvarchar", result.Columns[1].Type);
        Assert.Equal(3, result.Rows.Count);
        Assert.Equal("Asha", result.Rows[0][1]);
        Assert.Null(result.Rows[2][2]);
        Assert.False(result.Truncated);
    }

    [Fact]
    public async Task Caps_rows_and_flags_truncation()
    {
        db.SkipIfUnavailable();

        var result = await CreateExecutor().ExecuteAsync("SELECT Id FROM dbo.Orders", 10, Ct);

        Assert.Equal(10, result.Rows.Count);
        Assert.True(result.Truncated);
    }

    [Fact]
    public async Task An_exact_fit_is_not_reported_as_truncated()
    {
        db.SkipIfUnavailable();

        var result = await CreateExecutor().ExecuteAsync("SELECT Id FROM dbo.Customers", 3, Ct);

        Assert.Equal(3, result.Rows.Count);
        Assert.False(result.Truncated);
    }

    [Fact]
    public async Task Truncates_long_cell_values()
    {
        db.SkipIfUnavailable();

        var result = await CreateExecutor().ExecuteAsync("SELECT Notes FROM dbo.Orders WHERE Id = 50", 1, Ct);

        var notes = Assert.IsType<string>(result.Rows[0][0]);
        Assert.StartsWith(new string('x', 100), notes, StringComparison.Ordinal);
        Assert.EndsWith("(+1,900 chars)", notes, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("SELECT * FROM dbo.Customers")]
    [InlineData("SELECT c.* FROM dbo.Customers AS c")]
    [InlineData("SELECT x.* FROM (SELECT * FROM dbo.Customers) AS x")]
    public async Task Redacts_sensitive_columns_that_arrive_through_a_star(string sql)
    {
        db.SkipIfUnavailable();

        var result = await CreateExecutor().ExecuteAsync(sql, 10, Ct);

        var index = result.Columns.ToList().FindIndex(c => c.Name == "PasswordHash");
        Assert.True(index >= 0);
        Assert.All(result.Rows, row => Assert.Equal(QueryExecutor.Redacted, row[index]));
        Assert.Equal(["PasswordHash"], result.RedactedColumns);
    }

    [Fact]
    public async Task Rolls_back_even_if_a_write_gets_past_the_validator()
    {
        db.SkipIfUnavailable();
        var before = await db.ScalarAsync("SELECT COUNT(*) FROM dbo.Orders");

        // Calls the executor directly, skipping validation, to prove the second line of defence on its own.
        await CreateExecutor().ExecuteAsync(
            "INSERT dbo.Orders (CustomerId, Total) VALUES (1, 1); SELECT COUNT(*) FROM dbo.Orders", 10, Ct);

        Assert.Equal(before, await db.ScalarAsync("SELECT COUNT(*) FROM dbo.Orders"));
    }

    [Fact]
    public async Task Enforces_the_command_timeout()
    {
        db.SkipIfUnavailable();

        var executor = CreateExecutor(o => o.CommandTimeoutSeconds = 1);

        await Assert.ThrowsAsync<SqlException>(() => executor.ExecuteAsync("WAITFOR DELAY '00:00:05'; SELECT 1", 10, Ct));
    }
}
