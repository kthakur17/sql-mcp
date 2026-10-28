using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using SqlMcp.Configuration;
using SqlMcp.Safety;

namespace SqlMcp.Data;

public sealed record ResultColumn(string Name, string Type);

public sealed record QueryResult(
    IReadOnlyList<ResultColumn> Columns,
    IReadOnlyList<object?[]> Rows,
    bool Truncated,
    IReadOnlyList<string> RedactedColumns,
    long ElapsedMs);

/// <summary>
/// Runs an already-validated query. The validator is the first line of defence; this class is the second:
/// every query runs inside a transaction that is always rolled back, with a timeout and a row cap, and
/// result columns whose source column is sensitive are redacted (which covers <c>SELECT *</c>).
/// </summary>
public sealed class QueryExecutor(IOptions<SqlMcpOptions> options, AccessPolicy policy)
{
    public const string Redacted = "[REDACTED]";

    private static readonly HashSet<string> SpatialTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "geography", "geometry", "hierarchyid",
    };

    private readonly SqlMcpOptions _options = options.Value;

    public async Task<QueryResult> ExecuteAsync(string sql, int maxRows, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        await using var connection = new SqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        try
        {
            await using var command = new SqlCommand(sql, connection, transaction)
            {
                CommandTimeout = _options.CommandTimeoutSeconds,
            };

            // KeyInfo makes SQL Server report each column's base column, so "SELECT *" and aliases can still be redacted.
            await using var reader = await command.ExecuteReaderAsync(CommandBehavior.KeyInfo | CommandBehavior.SingleResult, cancellationToken);

            var schema = await reader.GetColumnSchemaAsync(cancellationToken);
            var visible = schema.Where(c => c.IsHidden != true).ToList();
            var redact = visible.Select(IsSensitive).ToArray();

            var rows = new List<object?[]>();
            while (rows.Count < maxRows && await reader.ReadAsync(cancellationToken))
            {
                var row = new object?[visible.Count];
                for (var i = 0; i < visible.Count; i++)
                {
                    row[i] = redact[i] ? Redacted : ReadCell(reader, visible[i]);
                }

                rows.Add(row);
            }

            var truncated = rows.Count == maxRows && await reader.ReadAsync(cancellationToken);
            if (truncated)
            {
                // Stops the server streaming the rest of a large result into a reader that will never read it.
                command.Cancel();
            }

            return new QueryResult(
                [.. visible.Select(c => new ResultColumn(c.ColumnName, c.DataTypeName ?? "unknown"))],
                rows,
                truncated,
                [.. visible.Where((_, i) => redact[i]).Select(c => c.ColumnName)],
                stopwatch.ElapsedMilliseconds);
        }
        finally
        {
            // A timeout or severe error can already have ended the transaction, which nulls its connection.
            if (transaction.Connection is not null)
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }
        }
    }

    private bool IsSensitive(DbColumn column)
    {
        return policy.IsSensitiveColumn(column.ColumnName)
            || (column.BaseColumnName is { Length: > 0 } baseName && policy.IsSensitiveColumn(baseName));
    }

    private object? ReadCell(SqlDataReader reader, DbColumn column)
    {
        var ordinal = column.ColumnOrdinal ?? throw new InvalidOperationException($"Column '{column.ColumnName}' has no ordinal.");

        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        if (column.DataTypeName is { } typeName && SpatialTypes.Contains(typeName.Split('.')[^1]))
        {
            return $"<{typeName}: CAST it to nvarchar(max) to read it>";
        }

        var value = reader.GetValue(ordinal);
        return value switch
        {
            string s => Truncate(s),
            byte[] bytes => $"<binary, {bytes.Length:N0} bytes>",
            DateTime dt => dt.ToString("O", CultureInfo.InvariantCulture),
            DateTimeOffset dto => dto.ToString("O", CultureInfo.InvariantCulture),
            TimeSpan ts => ts.ToString("c", CultureInfo.InvariantCulture),
            Guid g => g.ToString(),
            bool or byte or short or int or long or decimal or double or float => value,
            _ => Truncate(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty),
        };
    }

    private string Truncate(string value)
    {
        return value.Length <= _options.MaxCellLength
            ? value
            : $"{value[.._options.MaxCellLength]}… (+{value.Length - _options.MaxCellLength:N0} chars)";
    }
}
