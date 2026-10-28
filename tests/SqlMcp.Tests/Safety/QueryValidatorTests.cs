using SqlMcp.Configuration;
using SqlMcp.Safety;

namespace SqlMcp.Tests.Safety;

public class QueryValidatorTests
{
    private static QueryValidator CreateValidator(Action<SqlMcpOptions>? configure = null)
    {
        var options = new SqlMcpOptions { ConnectionString = "unused" };
        configure?.Invoke(options);
        return new QueryValidator(new AccessPolicy(options));
    }

    [Theory]
    [InlineData("SELECT 1")]
    [InlineData("SELECT TOP 10 * FROM dbo.Orders WHERE Total > 100 ORDER BY Total DESC;")]
    [InlineData("WITH big AS (SELECT CustomerId FROM dbo.Orders WHERE Total > 100) SELECT * FROM big")]
    [InlineData("SELECT Name FROM dbo.Customers UNION ALL SELECT Name FROM dbo.Suppliers")]
    [InlineData("SELECT CustomerId, SUM(Total) OVER (PARTITION BY CustomerId) FROM dbo.Orders")]
    [InlineData("SELECT * FROM dbo.Orders WITH (NOLOCK)")]
    [InlineData("-- a comment\nSELECT Name FROM dbo.Customers /* DROP TABLE dbo.Customers */")]
    [InlineData("SELECT 'DELETE FROM dbo.Orders; DROP TABLE dbo.Customers' AS Text")]
    [InlineData("SELECT o.* FROM dbo.Orders o CROSS APPLY OPENJSON(o.Notes) j")]
    public void Allows_read_only_selects(string sql)
    {
        var result = CreateValidator().Validate(sql);

        Assert.True(result.IsAllowed, string.Join(" | ", result.Violations));
    }

    [Theory]
    [InlineData("", "empty")]
    [InlineData("SELEC * FROM dbo.Orders", "Syntax error")]
    [InlineData("DELETE FROM dbo.Orders", "Only SELECT")]
    [InlineData("UPDATE dbo.Orders SET Total = 0", "Only SELECT")]
    [InlineData("TRUNCATE TABLE dbo.Orders", "Only SELECT")]
    [InlineData("DROP TABLE dbo.Orders", "Only SELECT")]
    [InlineData("EXEC xp_cmdshell 'dir'", "Only SELECT")]
    [InlineData("WAITFOR DELAY '00:10:00'", "Only SELECT")]
    [InlineData("WITH x AS (SELECT 1 AS a) DELETE FROM dbo.Orders", "Only SELECT")]
    [InlineData("SELECT 1; DROP TABLE dbo.Orders", "Exactly one statement")]
    [InlineData("SELECT 1 /* ; */ ; /**/ DELETE dbo.Orders", "Exactly one statement")]
    [InlineData("SELECT 1\nGO\nSELECT 2", "Exactly one statement")]
    [InlineData("BEGIN TRAN; SELECT 1", "Exactly one statement")]
    [InlineData("SELECT * INTO dbo.OrdersCopy FROM dbo.Orders", "SELECT ... INTO")]
    [InlineData("SELECT * FROM OPENROWSET('SQLNCLI', 'Server=evil;Trusted_Connection=yes', 'SELECT 1') AS r", "OPENROWSET")]
    [InlineData("SELECT * FROM OPENQUERY(LinkedServer, 'SELECT 1')", "OPENQUERY")]
    [InlineData("SELECT * FROM OPENDATASOURCE('SQLNCLI', 'Data Source=evil').db.dbo.t", "OPENDATASOURCE")]
    [InlineData("SELECT * FROM LinkedServer.Sales.dbo.Orders", "Linked-server")]
    [InlineData("SELECT name FROM master.sys.sql_logins", "Cross-database")]
    [InlineData("SELECT NEXT VALUE FOR dbo.OrderNumbers", "NEXT VALUE FOR")]
    [InlineData("SELECT * FROM dbo.Orders WITH (TABLOCKX)", "TABLOCKX")]
    [InlineData("SELECT * FROM dbo.Orders WITH (UPDLOCK, HOLDLOCK)", "UPDLOCK")]
    [InlineData("SELECT * FROM #scratch", "Temporary tables")]
    [InlineData("SELECT * FROM ##shared", "Temporary tables")]
    public void Rejects_anything_that_is_not_a_single_safe_select(string sql, string expected)
    {
        var result = CreateValidator().Validate(sql);

        Assert.False(result.IsAllowed);
        Assert.Contains(result.Violations, v => v.Contains(expected, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Rejects_queries_over_the_length_limit()
    {
        var sql = "SELECT 1 AS a" + new string(' ', QueryValidator.MaxQueryLength);

        var result = CreateValidator().Validate(sql);

        Assert.False(result.IsAllowed);
    }

    [Theory]
    [InlineData("SELECT PasswordHash FROM dbo.Customers")]
    [InlineData("SELECT Id FROM dbo.Customers WHERE PasswordHash LIKE 'a%'")]
    [InlineData("SELECT UPPER(c.ApiKey) FROM dbo.Customers AS c")]
    [InlineData("SELECT x.p FROM (SELECT c.[RefreshToken] AS p FROM dbo.Customers c) AS x")]
    [InlineData("SELECT Id FROM dbo.Customers ORDER BY SSN")]
    public void Rejects_explicit_references_to_sensitive_columns(string sql)
    {
        var result = CreateValidator().Validate(sql);

        Assert.False(result.IsAllowed);
        Assert.Contains(result.Violations, v => v.Contains("sensitive", StringComparison.Ordinal));
    }

    [Fact]
    public void Custom_sensitive_patterns_add_to_the_built_in_ones()
    {
        var validator = CreateValidator(o => o.SensitiveColumns.Add("DateOfBirth"));

        Assert.False(validator.Validate("SELECT DateOfBirth FROM dbo.People").IsAllowed);
        Assert.False(validator.Validate("SELECT Password FROM dbo.People").IsAllowed);
    }

    [Theory]
    [InlineData("SELECT * FROM dbo.Secrets")]
    [InlineData("SELECT * FROM [dbo].[Secrets]")]
    [InlineData("SELECT * FROM Secrets")]
    [InlineData("SELECT * FROM audit.Log")]
    [InlineData("SELECT * FROM dbo.Orders WHERE Id IN (SELECT OrderId FROM audit.Log)")]
    [InlineData("SELECT * FROM dbo.Orders o JOIN audit.Log l ON l.OrderId = o.Id")]
    [InlineData("SELECT * FROM audit.fn_Events(1)")]
    public void Rejects_tables_on_the_deny_list_wherever_they_appear(string sql)
    {
        var validator = CreateValidator(o =>
        {
            o.DeniedTables.Add("dbo.Secrets");
            o.DeniedTables.Add("audit.*");
        });

        var result = validator.Validate(sql);

        Assert.False(result.IsAllowed);
        Assert.Contains(result.Violations, v => v.Contains("denied by policy", StringComparison.Ordinal));
    }

    [Fact]
    public void A_cte_that_shadows_a_denied_table_name_is_allowed()
    {
        var validator = CreateValidator(o => o.DeniedTables.Add("dbo.Secrets"));

        var result = validator.Validate("WITH Secrets AS (SELECT 1 AS a) SELECT a FROM Secrets");

        Assert.True(result.IsAllowed);
    }

    [Fact]
    public void A_schema_allow_list_rejects_other_schemas_and_unqualified_names()
    {
        var validator = CreateValidator(o => o.AllowedSchemas.Add("sales"));

        Assert.True(validator.Validate("SELECT * FROM sales.Orders").IsAllowed);
        Assert.False(validator.Validate("SELECT * FROM dbo.Orders").IsAllowed);
        Assert.Contains("Qualify", validator.Validate("SELECT * FROM Orders").Violations[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Cross_database_reads_can_be_enabled()
    {
        var validator = CreateValidator(o => o.AllowCrossDatabase = true);

        Assert.True(validator.Validate("SELECT * FROM Reporting.dbo.Sales").IsAllowed);
        Assert.False(validator.Validate("SELECT * FROM Linked.Reporting.dbo.Sales").IsAllowed);
    }

    [Fact]
    public void Reports_the_tables_a_query_reads()
    {
        var result = CreateValidator().Validate(
            "SELECT * FROM dbo.Orders o JOIN sales.Customers c ON c.Id = o.CustomerId WHERE o.Id IN (SELECT OrderId FROM dbo.Orders)");

        Assert.Equal(["dbo.Orders", "sales.Customers"], result.Tables);
    }
}
