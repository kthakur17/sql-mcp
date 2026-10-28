using System.ComponentModel.DataAnnotations;

namespace SqlMcp.Configuration;

public sealed class SqlMcpOptions
{
    public const string SectionName = "SqlMcp";

    public static readonly IReadOnlyList<string> BuiltInSensitiveColumns =
    [
        "*password*", "*passwd*", "*secret*", "*token*", "*salt*", "*apikey*", "*api_key*", "ssn", "*_ssn",
    ];

    [Required(ErrorMessage = "SqlMcp:ConnectionString is required.")]
    public string ConnectionString { get; set; } = string.Empty;

    [Range(1, 10_000)]
    public int MaxRows { get; set; } = 200;

    [Range(1, 600)]
    public int CommandTimeoutSeconds { get; set; } = 30;

    [Range(16, 100_000)]
    public int MaxCellLength { get; set; } = 1_000;

    /// <summary>When non-empty, only tables in these schemas can be listed or queried.</summary>
    public IList<string> AllowedSchemas { get; } = [];

    /// <summary>Glob patterns over <c>schema.table</c>, e.g. <c>dbo.Users</c> or <c>audit.*</c>.</summary>
    public IList<string> DeniedTables { get; } = [];

    /// <summary>Glob patterns over column names, added to <see cref="BuiltInSensitiveColumns"/>.</summary>
    public IList<string> SensitiveColumns { get; } = [];

    public bool AllowCrossDatabase { get; set; }

    public string? AuditLogPath { get; set; }
}
