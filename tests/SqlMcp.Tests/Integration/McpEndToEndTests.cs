using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace SqlMcp.Tests.Integration;

/// <summary>Starts the real server as a child process and talks to it over stdio, exactly as Claude Desktop or Claude Code would.</summary>
[Collection(DatabaseGroup.Name)]
public sealed class McpEndToEndTests(TestDatabase db) : IAsyncDisposable
{
    private readonly string _auditPath = Path.Combine(Path.GetTempPath(), $"sql-mcp-audit-{Guid.NewGuid():N}.jsonl");
    private McpClient? _client;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Exposes_the_five_read_only_tools()
    {
        db.SkipIfUnavailable();
        var client = await ConnectAsync();

        var tools = await client.ListToolsAsync(cancellationToken: Ct);

        Assert.Equal(
            ["describe_table", "explain_query", "list_tables", "run_query", "server_info"],
            tools.Select(t => t.Name).Order(StringComparer.Ordinal));
        Assert.All(tools, t => Assert.True(t.ProtocolTool.Annotations?.ReadOnlyHint));
    }

    [Fact]
    public async Task Runs_a_query_and_returns_json_rows()
    {
        db.SkipIfUnavailable();
        var client = await ConnectAsync();

        var result = await client.CallToolAsync(
            "run_query",
            new Dictionary<string, object?> { ["sql"] = "SELECT COUNT(*) AS Orders FROM dbo.Orders" },
            cancellationToken: Ct);

        Assert.NotEqual(true, result.IsError);
        using var json = JsonDocument.Parse(Text(result));
        Assert.Equal(50, json.RootElement.GetProperty("rows")[0][0].GetInt32());
    }

    [Fact]
    public async Task Rejects_a_write_with_a_reason_the_agent_can_act_on_and_audits_it()
    {
        db.SkipIfUnavailable();
        var client = await ConnectAsync();

        var result = await client.CallToolAsync(
            "run_query",
            new Dictionary<string, object?> { ["sql"] = "DELETE FROM dbo.Orders" },
            cancellationToken: Ct);

        Assert.True(result.IsError);
        Assert.Contains("Only SELECT queries are allowed", Text(result), StringComparison.Ordinal);
        Assert.Equal(50, await db.ScalarAsync("SELECT COUNT(*) FROM dbo.Orders"));

        var audit = await File.ReadAllLinesAsync(_auditPath, Ct);
        using var entry = JsonDocument.Parse(audit[^1]);
        Assert.Equal("rejected", entry.RootElement.GetProperty("outcome").GetString());
        Assert.Equal("DELETE FROM dbo.Orders", entry.RootElement.GetProperty("sql").GetString());
    }

    [Fact]
    public async Task Hides_denied_tables_from_describe()
    {
        db.SkipIfUnavailable();
        var client = await ConnectAsync();

        var result = await client.CallToolAsync(
            "describe_table",
            new Dictionary<string, object?> { ["table"] = "audit.Events" },
            cancellationToken: Ct);

        Assert.True(result.IsError);
        Assert.Contains("not found or is not accessible", Text(result), StringComparison.Ordinal);
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is not null)
        {
            await _client.DisposeAsync();
        }

        File.Delete(_auditPath);
    }

    private static string Text(CallToolResult result) =>
        string.Concat(result.Content.OfType<TextContentBlock>().Select(c => c.Text));

    private async Task<McpClient> ConnectAsync()
    {
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "sql-mcp",
            Command = "dotnet",
            Arguments =
            [
                Path.Combine(AppContext.BaseDirectory, "SqlMcp.dll"),
                $"--SqlMcp:ConnectionString={db.ConnectionString}",
                "--SqlMcp:DeniedTables:0=audit.*",
                $"--SqlMcp:AuditLogPath={_auditPath}",
            ],
        });

        _client = await McpClient.CreateAsync(transport, cancellationToken: Ct);
        return _client;
    }
}
