using SqlMcp.Data;

namespace SqlMcp.Tests.Integration;

[Collection(DatabaseGroup.Name)]
public class PlanExplainerIntegrationTests(TestDatabase db)
{
    [Fact]
    public async Task Explains_without_executing()
    {
        db.SkipIfUnavailable();
        var explainer = new PlanExplainer(db.CreateOptions());

        var plan = await explainer.ExplainAsync(
            "SELECT c.Name, SUM(o.Total) FROM dbo.Customers c JOIN dbo.Orders o ON o.CustomerId = c.Id GROUP BY c.Name",
            TestContext.Current.CancellationToken);

        Assert.True(plan.EstimatedCost > 0);
        Assert.NotEmpty(plan.CostliestOperators);
        Assert.Contains(plan.CostliestOperators, o => o.Target?.Contains("Orders", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task Leaves_the_pooled_connection_executing_normally()
    {
        db.SkipIfUnavailable();
        var options = db.CreateOptions();
        await new PlanExplainer(options).ExplainAsync("SELECT 1", TestContext.Current.CancellationToken);

        var result = await new QueryExecutor(options, TestDatabase.CreatePolicy(options))
            .ExecuteAsync("SELECT 42 AS Answer", 1, TestContext.Current.CancellationToken);

        Assert.Equal(42, result.Rows[0][0]);
    }
}
