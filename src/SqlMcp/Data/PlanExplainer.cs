using System.Globalization;
using System.Xml.Linq;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using SqlMcp.Configuration;

namespace SqlMcp.Data;

public sealed record PlanOperator(string Operator, string? Target, double EstimatedRows, double CostPercent);

public sealed record MissingIndexSuggestion(string Table, double ImpactPercent, string Suggestion);

public sealed record PlanSummary(
    double EstimatedCost,
    double EstimatedRows,
    IReadOnlyList<PlanOperator> CostliestOperators,
    IReadOnlyList<MissingIndexSuggestion> MissingIndexes,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Asks SQL Server for the estimated plan without running the query, then condenses the showplan XML
/// (often tens of kilobytes) into the handful of facts an agent needs to decide whether to rewrite it.
/// </summary>
public sealed class PlanExplainer(IOptions<SqlMcpOptions> options)
{
    private const int TopOperatorCount = 5;
    private const double LargeScanRows = 100_000;

    private static readonly XNamespace Ns = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";

    private readonly SqlMcpOptions _options = options.Value;

    public async Task<PlanSummary> ExplainAsync(string sql, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        await SetShowPlanAsync(connection, on: true, cancellationToken);
        try
        {
            await using var command = new SqlCommand(sql, connection) { CommandTimeout = _options.CommandTimeoutSeconds };
            var xml = await command.ExecuteScalarAsync(cancellationToken) as string
                ?? throw new InvalidOperationException("SQL Server returned no plan.");
            return Summarize(xml);
        }
        finally
        {
            await SetShowPlanAsync(connection, on: false, CancellationToken.None);
        }
    }

    internal static PlanSummary Summarize(string showPlanXml)
    {
        var statement = XDocument.Parse(showPlanXml).Descendants(Ns + "StmtSimple").FirstOrDefault()
            ?? throw new InvalidOperationException("The plan contains no statement.");

        var totalCost = Number(statement, "StatementSubTreeCost");
        var relOps = statement.Descendants(Ns + "RelOp").ToList();

        var operators = relOps
            .Select(op => new PlanOperator(
                op.Attribute("PhysicalOp")?.Value ?? "Unknown",
                ObjectName(op),
                Number(op, "EstimateRows"),
                totalCost > 0 ? Math.Round(OwnCost(op) / totalCost * 100, 1) : 0))
            .OrderByDescending(o => o.CostPercent)
            .Take(TopOperatorCount)
            .ToList();

        var warnings = statement.Descendants(Ns + "Warnings")
            .SelectMany(w => w.Elements())
            .Select(DescribeWarning)
            .Concat(relOps
                .Where(op => op.Attribute("PhysicalOp")?.Value is "Table Scan" or "Clustered Index Scan" or "Index Scan"
                    && Number(op, "TableCardinality") >= LargeScanRows)
                .Select(op => $"Full scan of {ObjectName(op)} (~{Number(op, "TableCardinality"):N0} rows)."))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var missingIndexes = statement.Descendants(Ns + "MissingIndexGroup")
            .SelectMany(group => group.Elements(Ns + "MissingIndex").Select(index => ToSuggestion(index, Number(group, "Impact"))))
            .ToList();

        return new PlanSummary(totalCost, Number(statement, "StatementEstRows"), operators, missingIndexes, warnings);
    }

    private static async Task SetShowPlanAsync(SqlConnection connection, bool on, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(on ? "SET SHOWPLAN_XML ON;" : "SET SHOWPLAN_XML OFF;", connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static double OwnCost(XElement relOp)
    {
        var childCost = relOp.Elements().SelectMany(e => e.Elements(Ns + "RelOp")).Sum(c => Number(c, "EstimatedTotalSubtreeCost"));
        return Math.Max(0, Number(relOp, "EstimatedTotalSubtreeCost") - childCost);
    }

    private static string? ObjectName(XElement relOp)
    {
        var obj = relOp.Elements().SelectMany(e => e.Elements(Ns + "Object")).FirstOrDefault();
        if (obj is null)
        {
            return null;
        }

        var name = $"{obj.Attribute("Schema")?.Value}.{obj.Attribute("Table")?.Value}";
        return obj.Attribute("Index")?.Value is { } index ? $"{name} {index}" : name;
    }

    private static string DescribeWarning(XElement warning)
    {
        var details = string.Join(", ", warning.Attributes().Select(a => $"{a.Name.LocalName}={a.Value}"));
        return details.Length == 0 ? warning.Name.LocalName : $"{warning.Name.LocalName} ({details})";
    }

    private static MissingIndexSuggestion ToSuggestion(XElement index, double impact)
    {
        var schema = index.Attribute("Schema")?.Value ?? "[dbo]";
        var table = index.Attribute("Table")?.Value ?? "[?]";

        IEnumerable<string> Columns(string usage) => index.Elements(Ns + "ColumnGroup")
            .Where(g => g.Attribute("Usage")?.Value == usage)
            .SelectMany(g => g.Elements(Ns + "Column"))
            .Select(c => c.Attribute("Name")?.Value ?? "?");

        var keys = Columns("EQUALITY").Concat(Columns("INEQUALITY")).ToList();
        var includes = Columns("INCLUDE").ToList();
        var indexName = $"IX_{table.Trim('[', ']')}_{string.Join('_', keys.Select(k => k.Trim('[', ']')))}";

        var suggestion = $"CREATE INDEX [{indexName}] ON {schema}.{table} ({string.Join(", ", keys)})";
        if (includes.Count > 0)
        {
            suggestion += $" INCLUDE ({string.Join(", ", includes)})";
        }

        return new MissingIndexSuggestion($"{schema}.{table}", Math.Round(impact, 1), suggestion + ";");
    }

    private static double Number(XElement element, string attribute)
    {
        return double.TryParse(element.Attribute(attribute)?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : 0;
    }
}
