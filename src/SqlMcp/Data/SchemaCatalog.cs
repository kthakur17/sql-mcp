using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using SqlMcp.Configuration;
using SqlMcp.Safety;

namespace SqlMcp.Data;

public sealed record TableSummary(string Schema, string Name, string Kind, long? ApproxRows, string? Description);

public sealed record ColumnInfo(
    string Name,
    string Type,
    bool Nullable,
    bool Identity,
    bool Computed,
    string? Default,
    string? Description,
    bool Sensitive);

public sealed record IndexInfo(string Name, string Kind, bool Unique, bool PrimaryKey, string KeyColumns, string? IncludedColumns);

public sealed record ForeignKeyInfo(string Name, string FromTable, string FromColumns, string ToTable, string ToColumns);

public sealed record TableDetails(
    string Schema,
    string Name,
    string Kind,
    string? Description,
    IReadOnlyList<ColumnInfo> Columns,
    IReadOnlyList<IndexInfo> Indexes,
    IReadOnlyList<ForeignKeyInfo> ForeignKeys);

public sealed record ServerInfo(
    string Database,
    string Login,
    string Version,
    string Edition,
    IReadOnlyList<string> PrivilegeWarnings);

/// <summary>Reads the database's own catalog views, filtered through the <see cref="AccessPolicy"/> so denied tables never appear.</summary>
public sealed class SchemaCatalog(IOptions<SqlMcpOptions> options, AccessPolicy policy)
{
    private const string ListTablesSql = """
        SELECT s.name, o.name, o.type_desc,
               (SELECT SUM(p.rows) FROM sys.partitions p WHERE p.object_id = o.object_id AND p.index_id IN (0, 1)),
               CAST(ep.value AS nvarchar(1000))
        FROM sys.objects o
        JOIN sys.schemas s ON s.schema_id = o.schema_id
        LEFT JOIN sys.extended_properties ep
               ON ep.class = 1 AND ep.major_id = o.object_id AND ep.minor_id = 0 AND ep.name = N'MS_Description'
        WHERE o.type IN ('U', 'V') AND o.is_ms_shipped = 0 AND (@schema IS NULL OR s.name = @schema)
        ORDER BY s.name, o.name;
        """;

    private const string ObjectSql = """
        SELECT o.object_id, o.type_desc, CAST(ep.value AS nvarchar(1000))
        FROM sys.objects o
        JOIN sys.schemas s ON s.schema_id = o.schema_id
        LEFT JOIN sys.extended_properties ep
               ON ep.class = 1 AND ep.major_id = o.object_id AND ep.minor_id = 0 AND ep.name = N'MS_Description'
        WHERE s.name = @schema AND o.name = @table AND o.type IN ('U', 'V');
        """;

    private const string ColumnsSql = """
        SELECT c.name, t.name, c.max_length, c.precision, c.scale, c.is_nullable, c.is_identity, c.is_computed,
               dc.definition, CAST(ep.value AS nvarchar(1000))
        FROM sys.columns c
        JOIN sys.types t ON t.user_type_id = c.user_type_id
        LEFT JOIN sys.default_constraints dc ON dc.object_id = c.default_object_id
        LEFT JOIN sys.extended_properties ep
               ON ep.class = 1 AND ep.major_id = c.object_id AND ep.minor_id = c.column_id AND ep.name = N'MS_Description'
        WHERE c.object_id = @objectId
        ORDER BY c.column_id;
        """;

    private const string IndexesSql = """
        SELECT i.name, i.type_desc, i.is_unique, i.is_primary_key,
               (SELECT STRING_AGG(c.name, N', ') WITHIN GROUP (ORDER BY ic.key_ordinal)
                FROM sys.index_columns ic
                JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 0),
               (SELECT STRING_AGG(c.name, N', ') WITHIN GROUP (ORDER BY ic.index_column_id)
                FROM sys.index_columns ic
                JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 1)
        FROM sys.indexes i
        WHERE i.object_id = @objectId AND i.type > 0
        ORDER BY i.is_primary_key DESC, i.name;
        """;

    private const string ForeignKeysSql = """
        SELECT fk.name,
               OBJECT_SCHEMA_NAME(fk.parent_object_id), OBJECT_NAME(fk.parent_object_id),
               STRING_AGG(pc.name, N', ') WITHIN GROUP (ORDER BY fkc.constraint_column_id),
               OBJECT_SCHEMA_NAME(fk.referenced_object_id), OBJECT_NAME(fk.referenced_object_id),
               STRING_AGG(rc.name, N', ') WITHIN GROUP (ORDER BY fkc.constraint_column_id)
        FROM sys.foreign_keys fk
        JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id = fk.object_id
        JOIN sys.columns pc ON pc.object_id = fkc.parent_object_id AND pc.column_id = fkc.parent_column_id
        JOIN sys.columns rc ON rc.object_id = fkc.referenced_object_id AND rc.column_id = fkc.referenced_column_id
        WHERE fk.parent_object_id = @objectId OR fk.referenced_object_id = @objectId
        GROUP BY fk.name, fk.parent_object_id, fk.referenced_object_id
        ORDER BY fk.name;
        """;

    private const string ServerInfoSql = """
        SELECT DB_NAME(), SUSER_SNAME(), CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(128)),
               CAST(SERVERPROPERTY('Edition') AS nvarchar(128)),
               IS_SRVROLEMEMBER('sysadmin'), IS_ROLEMEMBER('db_owner'), IS_ROLEMEMBER('db_datawriter'),
               IS_ROLEMEMBER('db_ddladmin'),
               HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'INSERT'), HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'UPDATE'),
               HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'DELETE'), HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'EXECUTE'),
               HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'ALTER');
        """;

    private readonly string _connectionString = options.Value.ConnectionString;

    public async Task<IReadOnlyList<TableSummary>> ListTablesAsync(string? schema, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = new SqlCommand(ListTablesSql, connection);
        command.Parameters.AddWithValue("@schema", (object?)schema ?? DBNull.Value);

        var tables = new List<TableSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var table = new TableSummary(
                reader.GetString(0),
                reader.GetString(1),
                ToKind(reader.GetString(2)),
                reader.IsDBNull(3) ? null : reader.GetInt64(3),
                reader.IsDBNull(4) ? null : reader.GetString(4));

            if (policy.IsTableAllowed(table.Schema, table.Name))
            {
                tables.Add(table);
            }
        }

        return tables;
    }

    /// <summary>Returns null both when the table does not exist and when policy hides it, so denied tables cannot be probed for.</summary>
    public async Task<TableDetails?> DescribeTableAsync(string schema, string table, CancellationToken cancellationToken)
    {
        if (!policy.IsTableAllowed(schema, table))
        {
            return null;
        }

        await using var connection = await OpenAsync(cancellationToken);

        int objectId;
        string kind;
        string? description;
        await using (var command = new SqlCommand(ObjectSql, connection))
        {
            command.Parameters.AddWithValue("@schema", schema);
            command.Parameters.AddWithValue("@table", table);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            objectId = reader.GetInt32(0);
            kind = ToKind(reader.GetString(1));
            description = reader.IsDBNull(2) ? null : reader.GetString(2);
        }

        var columns = await ReadListAsync(connection, ColumnsSql, objectId, r => new ColumnInfo(
            r.GetString(0),
            FormatType(r.GetString(1), r.GetInt16(2), r.GetByte(3), r.GetByte(4)),
            r.GetBoolean(5),
            r.GetBoolean(6),
            r.GetBoolean(7),
            r.IsDBNull(8) ? null : r.GetString(8),
            r.IsDBNull(9) ? null : r.GetString(9),
            policy.IsSensitiveColumn(r.GetString(0))), cancellationToken);

        var indexes = await ReadListAsync(connection, IndexesSql, objectId, r => new IndexInfo(
            r.GetString(0),
            r.GetString(1),
            r.GetBoolean(2),
            r.GetBoolean(3),
            r.IsDBNull(4) ? string.Empty : r.GetString(4),
            r.IsDBNull(5) ? null : r.GetString(5)), cancellationToken);

        var foreignKeys = await ReadListAsync(connection, ForeignKeysSql, objectId, r => new
        {
            Key = new ForeignKeyInfo(
                r.GetString(0),
                $"{r.GetString(1)}.{r.GetString(2)}",
                r.GetString(3),
                $"{r.GetString(4)}.{r.GetString(5)}",
                r.GetString(6)),
            Visible = policy.IsTableAllowed(r.GetString(1), r.GetString(2)) && policy.IsTableAllowed(r.GetString(4), r.GetString(5)),
        }, cancellationToken);

        return new TableDetails(
            schema,
            table,
            kind,
            description,
            columns,
            indexes,
            [.. foreignKeys.Where(fk => fk.Visible).Select(fk => fk.Key)]);
    }

    public async Task<ServerInfo> GetServerInfoAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = new SqlCommand(ServerInfoSql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);

        var warnings = new List<string>();
        AddWarningIf(reader, 4, warnings, "The login is a sysadmin.");
        AddWarningIf(reader, 5, warnings, "The user is in db_owner.");
        AddWarningIf(reader, 6, warnings, "The user is in db_datawriter.");
        AddWarningIf(reader, 7, warnings, "The user is in db_ddladmin.");
        AddWarningIf(reader, 8, warnings, "The user has database-wide INSERT.");
        AddWarningIf(reader, 9, warnings, "The user has database-wide UPDATE.");
        AddWarningIf(reader, 10, warnings, "The user has database-wide DELETE.");
        AddWarningIf(reader, 11, warnings, "The user has database-wide EXECUTE.");
        AddWarningIf(reader, 12, warnings, "The user has ALTER on the database.");

        return new ServerInfo(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), warnings);
    }

    internal static string FormatType(string type, short maxLength, byte precision, byte scale)
    {
        return type.ToLowerInvariant() switch
        {
            "nvarchar" or "nchar" => maxLength == -1 ? $"{type}(max)" : $"{type}({maxLength / 2})",
            "varchar" or "char" or "varbinary" or "binary" => maxLength == -1 ? $"{type}(max)" : $"{type}({maxLength})",
            "decimal" or "numeric" => $"{type}({precision},{scale})",
            "datetime2" or "datetimeoffset" or "time" => $"{type}({scale})",
            _ => type,
        };
    }

    private static void AddWarningIf(SqlDataReader reader, int ordinal, List<string> warnings, string warning)
    {
        if (!reader.IsDBNull(ordinal) && reader.GetInt32(ordinal) == 1)
        {
            warnings.Add(warning);
        }
    }

    private static string ToKind(string typeDesc) => typeDesc == "VIEW" ? "view" : "table";

    private static async Task<List<T>> ReadListAsync<T>(
        SqlConnection connection,
        string sql,
        int objectId,
        Func<SqlDataReader, T> map,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@objectId", objectId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var items = new List<T>();
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(map(reader));
        }

        return items;
    }

    private async Task<SqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }
}
