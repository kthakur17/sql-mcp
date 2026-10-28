using SqlMcp.Data;

namespace SqlMcp.Tests.Integration;

[Collection(DatabaseGroup.Name)]
public class SchemaCatalogTests(TestDatabase db)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private SchemaCatalog CreateCatalog()
    {
        var options = db.CreateOptions();
        return new SchemaCatalog(options, TestDatabase.CreatePolicy(options));
    }

    [Fact]
    public async Task Lists_tables_with_row_counts_and_hides_denied_ones()
    {
        db.SkipIfUnavailable();

        var tables = await CreateCatalog().ListTablesAsync(null, Ct);

        Assert.Equal(["dbo.Customers", "dbo.Orders"], tables.Select(t => $"{t.Schema}.{t.Name}"));
        Assert.Equal(50, tables[1].ApproxRows);
        Assert.Equal("People who place orders.", tables[0].Description);
    }

    [Fact]
    public async Task Describes_columns_keys_and_relationships()
    {
        db.SkipIfUnavailable();

        var customers = await CreateCatalog().DescribeTableAsync("dbo", "Customers", Ct);

        Assert.NotNull(customers);
        var name = customers.Columns.Single(c => c.Name == "Name");
        Assert.Equal("nvarchar(100)", name.Type);
        Assert.False(name.Nullable);
        Assert.True(customers.Columns.Single(c => c.Name == "Id").Identity);
        Assert.True(customers.Columns.Single(c => c.Name == "PasswordHash").Sensitive);
        Assert.Equal("(sysutcdatetime())", customers.Columns.Single(c => c.Name == "CreatedAt").Default);

        var pk = Assert.Single(customers.Indexes);
        Assert.True(pk.PrimaryKey);

        // The FK from audit.Events points at Customers, but audit is denied, so it must not leak the table's name.
        var fk = Assert.Single(customers.ForeignKeys);
        Assert.Equal("FK_Orders_Customers", fk.Name);
    }

    [Fact]
    public async Task Reports_index_key_and_included_columns()
    {
        db.SkipIfUnavailable();

        var orders = await CreateCatalog().DescribeTableAsync("dbo", "Orders", Ct);

        var index = orders!.Indexes.Single(i => i.Name == "IX_Orders_CustomerId");
        Assert.Equal("CustomerId", index.KeyColumns);
        Assert.Equal("Total", index.IncludedColumns);
    }

    [Theory]
    [InlineData("audit", "Events")]
    [InlineData("dbo", "DoesNotExist")]
    public async Task Denied_and_missing_tables_look_the_same(string schema, string table)
    {
        db.SkipIfUnavailable();

        Assert.Null(await CreateCatalog().DescribeTableAsync(schema, table, Ct));
    }

    [Fact]
    public async Task Reports_server_details()
    {
        db.SkipIfUnavailable();

        var info = await CreateCatalog().GetServerInfoAsync(Ct);

        Assert.StartsWith("SqlMcpTests_", info.Database, StringComparison.Ordinal);
        Assert.NotEmpty(info.Version);
    }
}
