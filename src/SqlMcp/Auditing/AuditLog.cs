using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SqlMcp.Configuration;

namespace SqlMcp.Auditing;

public sealed record AuditEntry(
    DateTimeOffset Timestamp,
    string Tool,
    string Outcome,
    string? Sql = null,
    IReadOnlyList<string>? Violations = null,
    int? RowCount = null,
    long? ElapsedMs = null,
    string? Error = null);

/// <summary>Appends one JSON line per tool call, so every query an agent ran (or tried to run) can be reviewed later.</summary>
public sealed partial class AuditLog : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ILogger<AuditLog> _logger;

    public AuditLog(IOptions<SqlMcpOptions> options, ILogger<AuditLog> logger)
    {
        _logger = logger;
        FilePath = options.Value.AuditLogPath is { Length: > 0 } configured
            ? Path.GetFullPath(configured)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "sql-mcp", "audit.jsonl");
    }

    public string FilePath { get; }

    public async Task WriteAsync(AuditEntry entry)
    {
        var line = JsonSerializer.Serialize(entry, JsonOptions) + Environment.NewLine;

        await _gate.WaitAsync();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            await File.AppendAllTextAsync(FilePath, line);
        }
        catch (IOException ex)
        {
            // An unwritable audit file must not take the server down; it is reported on stderr instead.
            LogWriteFailed(ex, FilePath);
        }
        catch (UnauthorizedAccessException ex)
        {
            LogWriteFailed(ex, FilePath);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not write to the audit log at {Path}.")]
    private partial void LogWriteFailed(Exception exception, string path);
}
