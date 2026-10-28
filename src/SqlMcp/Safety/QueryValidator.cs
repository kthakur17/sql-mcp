using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SqlMcp.Safety;

public sealed record QueryValidationResult(bool IsAllowed, IReadOnlyList<string> Violations, IReadOnlyList<string> Tables)
{
    public static QueryValidationResult Rejected(params string[] violations) => new(false, violations, []);
}

/// <summary>
/// Parses a query with Microsoft's own T-SQL parser and allows it only if it is a single read-only SELECT
/// that stays inside the policy. Parsing, rather than pattern matching, is what makes comments, string
/// literals and odd whitespace unable to smuggle a second statement past the check.
/// </summary>
public sealed class QueryValidator(AccessPolicy policy)
{
    public const int MaxQueryLength = 20_000;

    public QueryValidationResult Validate(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            return QueryValidationResult.Rejected("The query is empty.");
        }

        if (sql.Length > MaxQueryLength)
        {
            return QueryValidationResult.Rejected($"The query is longer than {MaxQueryLength:N0} characters.");
        }

        var parser = new TSql170Parser(initialQuotedIdentifiers: true);
        using var reader = new StringReader(sql);
        var fragment = parser.Parse(reader, out var errors);

        if (errors.Count > 0)
        {
            return QueryValidationResult.Rejected(
                [.. errors.Select(e => $"Syntax error at line {e.Line}, column {e.Column}: {e.Message}")]);
        }

        var statements = ((TSqlScript)fragment).Batches.SelectMany(b => b.Statements).ToList();

        if (statements.Count != 1)
        {
            return QueryValidationResult.Rejected($"Exactly one statement is allowed; found {statements.Count}.");
        }

        if (statements[0] is not SelectStatement select)
        {
            return QueryValidationResult.Rejected(
                $"Only SELECT queries are allowed; found {Describe(statements[0])}.");
        }

        if (select.Into is not null)
        {
            return QueryValidationResult.Rejected("SELECT ... INTO creates a table and is not allowed.");
        }

        var cteNames = new CteNameCollector();
        select.Accept(cteNames);

        var visitor = new SafetyVisitor(policy, cteNames.Names);
        select.Accept(visitor);

        var violations = visitor.Violations.Distinct(StringComparer.Ordinal).ToList();
        var tables = visitor.Tables.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return new QueryValidationResult(violations.Count == 0, violations, tables);
    }

    private static string Describe(TSqlStatement statement)
    {
        var name = statement.GetType().Name;
        return name.EndsWith("Statement", StringComparison.Ordinal)
            ? $"a {name[..^"Statement".Length]} statement"
            : name;
    }

    private sealed class CteNameCollector : TSqlFragmentVisitor
    {
        public HashSet<string> Names { get; } = new(StringComparer.OrdinalIgnoreCase);

        public override void Visit(CommonTableExpression node)
        {
            Names.Add(node.ExpressionName.Value);
        }
    }

    private sealed class SafetyVisitor(AccessPolicy policy, HashSet<string> cteNames) : TSqlFragmentVisitor
    {
        private static readonly HashSet<TableHintKind> BlockingHints =
        [
            TableHintKind.HoldLock, TableHintKind.Serializable, TableHintKind.RepeatableRead,
            TableHintKind.UpdLock, TableHintKind.XLock, TableHintKind.TabLock, TableHintKind.TabLockX,
            TableHintKind.PagLock,
        ];

        public List<string> Violations { get; } = [];

        public List<string> Tables { get; } = [];

        public override void Visit(NamedTableReference node)
        {
            CheckObject(node.SchemaObject);

            foreach (var hint in node.TableHints.Where(h => BlockingHints.Contains(h.HintKind)))
            {
                Violations.Add($"The {hint.HintKind.ToString().ToUpperInvariant()} hint is not allowed because it can block other sessions.");
            }
        }

        public override void Visit(SchemaObjectFunctionTableReference node)
        {
            CheckObject(node.SchemaObject);
        }

        public override void Visit(OpenRowsetTableReference node) => RejectExternalAccess("OPENROWSET");

        public override void Visit(BulkOpenRowset node) => RejectExternalAccess("OPENROWSET(BULK ...)");

        public override void Visit(InternalOpenRowset node) => RejectExternalAccess("OPENROWSET");

        public override void Visit(OpenRowsetCosmos node) => RejectExternalAccess("OPENROWSET");

        public override void Visit(OpenQueryTableReference node) => RejectExternalAccess("OPENQUERY");

        public override void Visit(AdHocTableReference node) => RejectExternalAccess("OPENDATASOURCE");

        public override void Visit(NextValueForExpression node)
        {
            Violations.Add("NEXT VALUE FOR advances a sequence, which is a write.");
        }

        public override void Visit(ColumnReferenceExpression node)
        {
            if (node.ColumnType != ColumnType.Regular || node.MultiPartIdentifier is null)
            {
                return;
            }

            var column = node.MultiPartIdentifier.Identifiers[^1].Value;
            if (policy.IsSensitiveColumn(column))
            {
                Violations.Add($"Column '{column}' is marked sensitive and cannot be referenced. Remove it from the query.");
            }
        }

        private void RejectExternalAccess(string construct)
        {
            Violations.Add($"{construct} reads data from outside this database and is not allowed.");
        }

        private void CheckObject(SchemaObjectName name)
        {
            var display = string.Join('.', name.Identifiers.Select(i => i.Value));
            var table = name.BaseIdentifier.Value;

            if (name.ServerIdentifier is not null)
            {
                Violations.Add($"Linked-server references are not allowed: {display}.");
                return;
            }

            if (name.DatabaseIdentifier is not null && !policy.AllowCrossDatabase)
            {
                Violations.Add($"Cross-database references are not allowed: {display}.");
                return;
            }

            if (table.StartsWith('#'))
            {
                Violations.Add($"Temporary tables are not accessible: {display}.");
                return;
            }

            if (name.SchemaIdentifier is null)
            {
                if (cteNames.Contains(table))
                {
                    return;
                }

                if (policy.HasSchemaAllowList)
                {
                    Violations.Add($"Qualify '{table}' with its schema; a schema allow-list is configured.");
                }
                else if (!policy.IsUnqualifiedTableAllowed(table))
                {
                    Violations.Add($"Access to '{table}' is denied by policy.");
                }

                Tables.Add(table);
                return;
            }

            var schema = name.SchemaIdentifier.Value;
            if (!policy.IsTableAllowed(schema, table))
            {
                Violations.Add($"Access to '{schema}.{table}' is denied by policy.");
            }

            Tables.Add($"{schema}.{table}");
        }
    }
}
