using ModelContextProtocol;
using SqlMcp.Data;
using SqlMcp.Tools;

namespace SqlMcp.Tests.Data;

public class NameAndTypeFormattingTests
{
    [Theory]
    [InlineData("Orders", "dbo", "Orders")]
    [InlineData("sales.Orders", "sales", "Orders")]
    [InlineData("[sales].[Order Lines]", "sales", "Order Lines")]
    [InlineData(" sales . Orders ", "sales", "Orders")]
    public void Splits_table_names(string input, string schema, string table)
    {
        Assert.Equal((schema, table), DatabaseTools.SplitName(input));
    }

    [Fact]
    public void Rejects_names_with_more_than_two_parts()
    {
        Assert.Throws<McpException>(() => DatabaseTools.SplitName("db.dbo.Orders"));
    }

    [Theory]
    [InlineData("nvarchar", 200, 0, 0, "nvarchar(100)")]
    [InlineData("nvarchar", -1, 0, 0, "nvarchar(max)")]
    [InlineData("varchar", 50, 0, 0, "varchar(50)")]
    [InlineData("decimal", 9, 10, 2, "decimal(10,2)")]
    [InlineData("datetime2", 8, 27, 7, "datetime2(7)")]
    [InlineData("int", 4, 10, 0, "int")]
    public void Formats_sql_types(string type, short maxLength, byte precision, byte scale, string expected)
    {
        Assert.Equal(expected, SchemaCatalog.FormatType(type, maxLength, precision, scale));
    }
}
