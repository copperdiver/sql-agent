using Bunit;
using SqlAgent.Core;
using SqlAgent.Host.Components.Shared.Chat;
using SqlAgent.Storage;

namespace SqlAgent.Tests;

public class SqlBlockTests
{
    private static NlQueryResult Result(string sql) => NlQueryResult.Query(
        QueryExecutionResult.Ok(sql, new QueryResultSet(["id"], [new object?[] { 1 }], false), 4));

    [Fact]
    public void A_read_block_shows_dialect_run_and_result_rows()
    {
        using var ctx = new Bunit.BunitContext();
        var block = ctx.Render<SqlBlock>(p => p
            .Add(x => x.Result, Result("SELECT 1"))
            .Add(x => x.ProviderType, DatabaseProviderType.Postgres));

        Assert.Contains("PostgreSQL", block.Markup);
        Assert.Contains("SELECT 1", block.Find("pre").TextContent);
        Assert.Single(block.FindAll("tbody tr"));
        Assert.NotEmpty(block.FindAll("[data-testid=sql-block-run]"));
    }

    [Fact]
    public void A_pending_write_shows_operation_warning_and_runs_only_through_the_callback()
    {
        using var ctx = new Bunit.BunitContext();
        var pending = NlQueryResult.Confirmation("DROP TABLE orders", "DropTable");
        var runs = 0;
        var block = ctx.Render<SqlBlock>(p => p
            .Add(x => x.Result, pending)
            .Add(x => x.ProviderType, DatabaseProviderType.Postgres)
            .Add(x => x.OnRun, () => runs++));

        Assert.Contains("DropTable", block.Markup);
        Assert.Contains("confirmation", block.Markup, StringComparison.OrdinalIgnoreCase);
        block.Find("[data-testid=sql-block-run]").Click();
        Assert.Equal(1, runs);
    }
}
