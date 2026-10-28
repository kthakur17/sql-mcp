using Microsoft.Extensions.Options;
using SqlMcp.Configuration;

namespace SqlMcp.Safety;

/// <summary>Decides which tables and columns an agent may see. Shared by the validator, the executor and the schema tools.</summary>
public sealed class AccessPolicy
{
    private readonly IReadOnlyList<string> _allowedSchemas;
    private readonly IReadOnlyList<string> _deniedTables;
    private readonly IReadOnlyList<string> _sensitiveColumns;

    public AccessPolicy(IOptions<SqlMcpOptions> options)
        : this(options.Value)
    {
    }

    public AccessPolicy(SqlMcpOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _allowedSchemas = [.. options.AllowedSchemas];
        _deniedTables = [.. options.DeniedTables];
        _sensitiveColumns = [.. SqlMcpOptions.BuiltInSensitiveColumns, .. options.SensitiveColumns];
        AllowCrossDatabase = options.AllowCrossDatabase;
    }

    public bool AllowCrossDatabase { get; }

    public bool HasSchemaAllowList => _allowedSchemas.Count > 0;

    public bool IsSchemaAllowed(string schema)
    {
        return !HasSchemaAllowList || _allowedSchemas.Contains(schema, StringComparer.OrdinalIgnoreCase);
    }

    public bool IsTableAllowed(string schema, string table)
    {
        if (!IsSchemaAllowed(schema))
        {
            return false;
        }

        return !_deniedTables.Any(p => GlobPattern.IsMatch(p, $"{schema}.{table}"));
    }

    /// <summary>
    /// For a reference with no schema, SQL Server resolves the schema from the caller's default, which this
    /// process cannot know for certain, so the table is denied if a deny pattern matches it under any schema.
    /// </summary>
    public bool IsUnqualifiedTableAllowed(string table)
    {
        return !_deniedTables.Any(p =>
        {
            var dot = p.LastIndexOf('.');
            var tablePart = dot < 0 ? p : p[(dot + 1)..];
            return GlobPattern.IsMatch(tablePart, table);
        });
    }

    public bool IsSensitiveColumn(string column)
    {
        return _sensitiveColumns.Any(p => GlobPattern.IsMatch(p, column));
    }
}
