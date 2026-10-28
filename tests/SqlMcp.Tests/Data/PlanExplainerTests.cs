using SqlMcp.Data;

namespace SqlMcp.Tests.Data;

public class PlanExplainerTests
{
    private const string Plan = """
        <ShowPlanXML xmlns="http://schemas.microsoft.com/sqlserver/2004/07/showplan" Version="1.6">
          <BatchSequence><Batch><Statements>
            <StmtSimple StatementText="SELECT ..." StatementSubTreeCost="10" StatementEstRows="42">
              <QueryPlan>
                <Warnings><PlanAffectingConvert ConvertIssue="Seek Plan" Expression="CONVERT_IMPLICIT(nvarchar(50),[o].[Code],0)" /></Warnings>
                <MissingIndexes>
                  <MissingIndexGroup Impact="87.26">
                    <MissingIndex Database="[Shop]" Schema="[dbo]" Table="[Orders]">
                      <ColumnGroup Usage="EQUALITY"><Column Name="[CustomerId]" ColumnId="2" /></ColumnGroup>
                      <ColumnGroup Usage="INEQUALITY"><Column Name="[PlacedAt]" ColumnId="4" /></ColumnGroup>
                      <ColumnGroup Usage="INCLUDE"><Column Name="[Total]" ColumnId="3" /></ColumnGroup>
                    </MissingIndex>
                  </MissingIndexGroup>
                </MissingIndexes>
                <RelOp NodeId="0" PhysicalOp="Hash Match" EstimateRows="42" EstimatedTotalSubtreeCost="10">
                  <Hash>
                    <RelOp NodeId="1" PhysicalOp="Clustered Index Scan" EstimateRows="500000" EstimatedTotalSubtreeCost="7.5" TableCardinality="500000">
                      <IndexScan><Object Database="[Shop]" Schema="[dbo]" Table="[Orders]" Index="[PK_Orders]" /></IndexScan>
                    </RelOp>
                    <RelOp NodeId="2" PhysicalOp="Index Seek" EstimateRows="10" EstimatedTotalSubtreeCost="0.5" TableCardinality="1000">
                      <IndexScan><Object Database="[Shop]" Schema="[dbo]" Table="[Customers]" Index="[IX_Customers_Name]" /></IndexScan>
                    </RelOp>
                  </Hash>
                </RelOp>
              </QueryPlan>
            </StmtSimple>
          </Statements></Batch></BatchSequence>
        </ShowPlanXML>
        """;

    [Fact]
    public void Summarizes_cost_and_ranks_operators_by_their_own_cost()
    {
        var summary = PlanExplainer.Summarize(Plan);

        Assert.Equal(10, summary.EstimatedCost);
        Assert.Equal(42, summary.EstimatedRows);
        Assert.Equal("Clustered Index Scan", summary.CostliestOperators[0].Operator);
        Assert.Equal(75, summary.CostliestOperators[0].CostPercent);
        Assert.Equal("[dbo].[Orders] [PK_Orders]", summary.CostliestOperators[0].Target);
        Assert.Equal(20, summary.CostliestOperators[1].CostPercent);
    }

    [Fact]
    public void Turns_missing_index_hints_into_create_index_statements()
    {
        var index = Assert.Single(PlanExplainer.Summarize(Plan).MissingIndexes);

        Assert.Equal(87.3, index.ImpactPercent);
        Assert.Equal(
            "CREATE INDEX [IX_Orders_CustomerId_PlacedAt] ON [dbo].[Orders] ([CustomerId], [PlacedAt]) INCLUDE ([Total]);",
            index.Suggestion);
    }

    [Fact]
    public void Reports_plan_warnings_and_large_scans()
    {
        var warnings = PlanExplainer.Summarize(Plan).Warnings;

        Assert.Contains(warnings, w => w.StartsWith("PlanAffectingConvert", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.Contains("Full scan of [dbo].[Orders]", StringComparison.Ordinal));
        Assert.DoesNotContain(warnings, w => w.Contains("Customers", StringComparison.Ordinal));
    }
}
