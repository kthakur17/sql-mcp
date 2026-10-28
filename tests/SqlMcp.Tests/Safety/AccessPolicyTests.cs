using SqlMcp.Configuration;
using SqlMcp.Safety;

namespace SqlMcp.Tests.Safety;

public class AccessPolicyTests
{
    [Theory]
    [InlineData("Password", true)]
    [InlineData("user_password", true)]
    [InlineData("PASSWORDHASH", true)]
    [InlineData("ApiKey", true)]
    [InlineData("RefreshToken", true)]
    [InlineData("ClientSecret", true)]
    [InlineData("SSN", true)]
    [InlineData("employee_ssn", true)]
    [InlineData("Assessment", false)]
    [InlineData("Email", false)]
    [InlineData("Name", false)]
    public void Built_in_patterns_flag_common_credential_columns(string column, bool expected)
    {
        var policy = new AccessPolicy(new SqlMcpOptions());

        Assert.Equal(expected, policy.IsSensitiveColumn(column));
    }

    [Theory]
    [InlineData("dbo", "Users", false)]
    [InlineData("DBO", "users", false)]
    [InlineData("audit", "Anything", false)]
    [InlineData("dbo", "Orders", true)]
    [InlineData("sales", "Users", true)]
    public void Deny_patterns_match_schema_and_table_case_insensitively(string schema, string table, bool expected)
    {
        var options = new SqlMcpOptions();
        options.DeniedTables.Add("dbo.Users");
        options.DeniedTables.Add("audit.*");

        Assert.Equal(expected, new AccessPolicy(options).IsTableAllowed(schema, table));
    }

    [Fact]
    public void An_unqualified_name_is_denied_if_it_is_denied_in_any_schema()
    {
        var options = new SqlMcpOptions();
        options.DeniedTables.Add("hr.Salaries");
        var policy = new AccessPolicy(options);

        Assert.False(policy.IsUnqualifiedTableAllowed("Salaries"));
        Assert.True(policy.IsUnqualifiedTableAllowed("Orders"));
    }
}
