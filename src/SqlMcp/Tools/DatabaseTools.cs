using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using SqlMcp.Auditing;
using SqlMcp.Configuration;
using SqlMcp.Data;
using SqlMcp.Safety;

namespace SqlMcp.Tools;

[McpServerToolType]
public sealed class DatabaseTools(
    SchemaCatalog catalog,
    QueryValidator validator,
    QueryExecutor executor,
    PlanExplainer explainer,
    AuditLog audit,
    IOptions<SqlMcpOptions> options)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly SqlMcpOptions _options = options.Value;

    [McpServerTool(Name = "server_info", Title = "Server info", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Shows the connected database, login and SQL Server version, the limits applied to every query, and warnings if the login has more privileges than read-only access needs. Call this first.")]
    public async Task<string> ServerInfoAsync(CancellationToken cancellationToken)
    {
        var info = await RunDatabaseCallAsync("server_info", () => catalog.GetServerInfoAsync(cancellationToken));
        return Serialize(new
        {
            info.Database,
            info.Login,
            info.Version,
            info.Edition,
            Limits = new { _options.MaxRows, _options.CommandTimeoutSeconds, _options.MaxCellLength },
            info.PrivilegeWarnings,
        });
    }

    [McpServerTool(Name = "list_tables", Title = "List tables", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Lists the tables and views you are allowed to query, with approximate row counts and any descriptions stored in the database.")]
    public async Task<string> ListTablesAsync(
        [Description("Only list objects in this schema, for example 'dbo' or 'sales'. Omit to list every schema.")] string? schema = null,
        CancellationToken cancellationToken = default)
    {
        var tables = await RunDatabaseCallAsync("list_tables", () => catalog.ListTablesAsync(schema, cancellationToken));
        return Serialize(tables);
    }

    [McpServerTool(Name = "describe_table", Title = "Describe table", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Describes one table or view: columns with types and nullability, primary key, indexes, and foreign keys in both directions. Columns flagged 'sensitive' cannot be queried. Use this before writing a query against a table you have not seen.")]
    public async Task<string> DescribeTableAsync(
        [Description("Table or view name, optionally schema-qualified: 'Orders' or 'sales.Orders'. An unqualified name is looked up in dbo.")] string table,
        CancellationToken cancellationToken = default)
    {
        var (schema, name) = SplitName(table);
        var details = await RunDatabaseCallAsync("describe_table", () => catalog.DescribeTableAsync(schema, name, cancellationToken))
            ?? throw new McpException($"Table '{schema}.{name}' was not found or is not accessible.");
        return Serialize(details);
    }

    [McpServerTool(Name = "run_query", Title = "Run query", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Runs one read-only T-SQL SELECT (CTEs allowed) and returns the rows. Writes, DDL, EXEC, multiple statements, SELECT INTO and cross-database or linked-server access are rejected. Results are capped; aggregate or filter in SQL instead of fetching everything.")]
    public async Task<string> RunQueryAsync(
        [Description("A single T-SQL SELECT statement.")] string sql,
        [Description("Maximum rows to return. Defaults to, and cannot exceed, the server's configured limit.")] int? maxRows = null,
        CancellationToken cancellationToken = default)
    {
        await EnsureAllowedAsync("run_query", sql);

        var limit = Math.Clamp(maxRows ?? _options.MaxRows, 1, _options.MaxRows);
        var result = await RunDatabaseCallAsync("run_query", () => executor.ExecuteAsync(sql, limit, cancellationToken), sql);

        await audit.WriteAsync(new AuditEntry(
            DateTimeOffset.UtcNow, "run_query", "allowed", sql, RowCount: result.Rows.Count, ElapsedMs: result.ElapsedMs));

        return Serialize(new
        {
            result.Columns,
            result.Rows,
            RowCount = result.Rows.Count,
            result.Truncated,
            RedactedColumns = result.RedactedColumns.Count > 0 ? result.RedactedColumns : null,
            result.ElapsedMs,
            Note = result.Truncated
                ? $"Only the first {limit} rows were returned. Narrow the query with WHERE, TOP or aggregation rather than raising the limit."
                : null,
        });
    }

    [McpServerTool(Name = "explain_query", Title = "Explain query", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Returns SQL Server's estimated execution plan for a SELECT without running it: estimated cost and rows, the costliest operators, large scans, and missing-index suggestions. Use it to check an expensive-looking query before running it.")]
    public async Task<string> ExplainQueryAsync(
        [Description("A single T-SQL SELECT statement.")] string sql,
        CancellationToken cancellationToken = default)
    {
        await EnsureAllowedAsync("explain_query", sql);

        var plan = await RunDatabaseCallAsync("explain_query", () => explainer.ExplainAsync(sql, cancellationToken), sql);
        await audit.WriteAsync(new AuditEntry(DateTimeOffset.UtcNow, "explain_query", "allowed", sql));
        return Serialize(plan);
    }

    internal static (string Schema, string Table) SplitName(string name)
    {
        var parts = name.Split('.', StringSplitOptions.TrimEntries)
            .Select(p => p.TrimStart('[').TrimEnd(']'))
            .ToArray();

        return parts.Length switch
        {
            1 => ("dbo", parts[0]),
            2 => (parts[0], parts[1]),
            _ => throw new McpException($"'{name}' is not a valid table name. Use 'table' or 'schema.table'."),
        };
    }

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Json);

    private async Task EnsureAllowedAsync(string tool, string sql)
    {
        var validation = validator.Validate(sql);
        if (validation.IsAllowed)
        {
            return;
        }

        await audit.WriteAsync(new AuditEntry(DateTimeOffset.UtcNow, tool, "rejected", sql, validation.Violations));
        throw new McpException("Query rejected:" + string.Concat(validation.Violations.Select(v => $"{Environment.NewLine}- {v}")));
    }

    private async Task<T> RunDatabaseCallAsync<T>(string tool, Func<Task<T>> call, string? sql = null)
    {
        try
        {
            return await call();
        }
        catch (SqlException ex)
        {
            await audit.WriteAsync(new AuditEntry(DateTimeOffset.UtcNow, tool, "failed", sql, Error: ex.Message));
            throw new McpException($"SQL Server error {ex.Number}: {ex.Message}", ex);
        }
    }
}
