using Bunit;
using Microsoft.JSInterop;
using SqlAgent.Host.Components.Shared;

namespace SqlAgent.Tests;

public class DataTableTests
{
    private static readonly IReadOnlyList<string> Columns = ["id", "value"];

    private static IReadOnlyList<IReadOnlyList<object?>> Rows(int count) =>
        Enumerable.Range(1, count).Select(i => (IReadOnlyList<object?>)[i, i == 2 ? null : $"row-{i}"]).ToList();

    [Fact]
    public void Renders_null_distinctly_and_pages_to_twenty_five_rows_by_default()
    {
        using var ctx = new Bunit.BunitContext();
        var table = ctx.Render<DataTable>(p => p
            .Add(x => x.Columns, Columns)
            .Add(x => x.Rows, Rows(30))
            .Add(x => x.TotalRowCount, 30));

        Assert.Equal(25, table.FindAll("tbody tr").Count);
        Assert.Contains("NULL", table.Markup);
        Assert.Contains("showing 1–25 of 30", table.Markup);
    }

    [Fact]
    public void Page_size_and_next_page_change_the_visible_rows()
    {
        using var ctx = new Bunit.BunitContext();
        var table = ctx.Render<DataTable>(p => p
            .Add(x => x.Columns, Columns)
            .Add(x => x.Rows, Rows(60))
            .Add(x => x.TotalRowCount, 60));

        table.Find("[data-testid=data-table-page-size]").Change("50");
        Assert.Equal(50, table.FindAll("tbody tr").Count);
        table.Find("[data-testid=data-table-next]").Click();

        Assert.Contains("showing 51–60 of 60", table.Markup);
        Assert.Contains("row-60", table.Markup);
    }

    [Fact]
    public void Long_values_expand_in_place_and_exports_use_the_visible_result()
    {
        using var ctx = new Bunit.BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var longValue = new string('x', 240);
        var table = ctx.Render<DataTable>(p => p
            .Add(x => x.Columns, new[] { "value" })
            .Add(x => x.Rows, new[] { (IReadOnlyList<object?>)[longValue] })
            .Add(x => x.TotalRowCount, 1));

        var cell = table.Find("tbody td:not(.row-number)");
        Assert.DoesNotContain(longValue, cell.TextContent);
        table.Find("[data-testid=cell-expand-0-0]").Click();
        Assert.Contains(longValue, table.Find("tbody td:not(.row-number)").TextContent);

        table.Find("[data-testid=data-table-export-csv]").Click();
        Assert.Equal("sqlAgentDownload", ctx.JSInterop.Invocations.Single().Identifier);
    }
}
