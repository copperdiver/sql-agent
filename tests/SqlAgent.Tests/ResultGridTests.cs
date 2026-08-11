using Bunit;
using SqlAgent.Core;
using SqlAgent.Host.Components.Shared;
using SqlAgent.Host.Web;

namespace SqlAgent.Tests;

public class ResultGridTests
{
    private static readonly IReadOnlyList<string> SuccessColumns = ["id", "name"];
    private static readonly IReadOnlyList<IReadOnlyList<object?>> SuccessRows =
        [new object?[] { 1, "a" }, new object?[] { 2, null }];

    private static QueryExecutionResult Success(bool truncated) => QueryExecutionResult.Ok(
        "SELECT 1",
        new QueryResultSet(SuccessColumns, SuccessRows, truncated),
        18);

    private static QueryExecutionResult SuccessWithValue(object? value) => QueryExecutionResult.Ok(
        "SELECT 1",
        new QueryResultSet(["v"], [new object?[] { value }], false),
        5);

    [Fact]
    public void Rows_and_columns_are_rendered()
    {
        using var ctx = new Bunit.TestContext();

        var grid = ctx.RenderComponent<ResultGrid>(p => p.Add(g => g.Result, Success(truncated: false)));

        Assert.Contains("id", grid.Markup);
        Assert.Contains("name", grid.Markup);
        Assert.Equal(2, grid.FindAll("tbody tr").Count);
    }

    [Fact]
    public void A_null_value_renders_as_NULL_not_as_an_empty_cell()
    {
        using var ctx = new Bunit.TestContext();

        var grid = ctx.RenderComponent<ResultGrid>(p => p.Add(g => g.Result, Success(truncated: false)));

        Assert.Contains("NULL", grid.Markup);
    }

    [Fact]
    public void Row_count_and_elapsed_time_are_shown()
    {
        using var ctx = new Bunit.TestContext();

        var grid = ctx.RenderComponent<ResultGrid>(p => p.Add(g => g.Result, Success(truncated: false)));

        Assert.Contains("2 rows", grid.Markup);
        Assert.Contains("18 ms", grid.Markup);
    }

    [Fact]
    public void Truncation_is_announced()
    {
        using var ctx = new Bunit.TestContext();

        var grid = ctx.RenderComponent<ResultGrid>(p => p.Add(g => g.Result, Success(truncated: true)));

        // A capped result is a normal outcome, not an error — but the user must know rows are missing.
        Assert.Contains("truncated", grid.Markup, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_policy_denial_renders_as_a_message_with_its_code()
    {
        using var ctx = new Bunit.TestContext();

        var message = ctx.RenderComponent<OutcomeMessage>(p => p
            .Add(m => m.Code, "policy_denied_readonly")
            .Add(m => m.Message, "Connection is read-only; 'UPDATE' would modify data."));

        Assert.Contains("policy_denied_readonly", message.Markup);
        Assert.Contains("read-only", message.Markup);
    }

    [Fact]
    public void A_cell_value_containing_HTML_renders_as_literal_text_not_markup()
    {
        // Guards against a future switch to MarkupString reintroducing an injection hole: cell values
        // must stay plain Razor interpolation, which HTML-encodes automatically.
        using var ctx = new Bunit.TestContext();
        const string payload = "<script>alert(1)</script>";

        var grid = ctx.RenderComponent<ResultGrid>(p => p.Add(g => g.Result, SuccessWithValue(payload)));

        Assert.DoesNotContain("<script>", grid.Markup);
        Assert.Equal(payload, grid.Find("td").TextContent);
    }

    [Fact]
    public void Export_CSV_button_asks_the_browser_to_download_the_csv_serialization_of_the_rows_on_screen()
    {
        // bUnit has no JS engine: this only proves the component asks the browser to download the right
        // filename, mime type, and payload. It does not prove a file actually lands on disk in a real browser.
        using var ctx = new Bunit.TestContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose; // component only needs the call recorded, not a real return value
        var grid = ctx.RenderComponent<ResultGrid>(p => p.Add(g => g.Result, Success(truncated: false)));

        var csvButton = grid.FindAll("button").Single(b => b.TextContent.Contains("CSV"));
        csvButton.Click();

        var invocation = ctx.JSInterop.VerifyInvoke("sqlAgentDownload");
        Assert.Equal("result.csv", invocation.Arguments[0]);
        Assert.Equal("text/csv", invocation.Arguments[1]);
        Assert.Equal(ResultExport.ToCsv(SuccessColumns, SuccessRows), invocation.Arguments[2]);
    }

    [Fact]
    public void Export_JSON_button_asks_the_browser_to_download_the_json_serialization_of_the_rows_on_screen()
    {
        using var ctx = new Bunit.TestContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var grid = ctx.RenderComponent<ResultGrid>(p => p.Add(g => g.Result, Success(truncated: false)));

        var jsonButton = grid.FindAll("button").Single(b => b.TextContent.Contains("JSON"));
        jsonButton.Click();

        var invocation = ctx.JSInterop.VerifyInvoke("sqlAgentDownload");
        Assert.Equal("result.json", invocation.Arguments[0]);
        Assert.Equal("application/json", invocation.Arguments[1]);
        Assert.Equal(ResultExport.ToJson(SuccessColumns, SuccessRows), invocation.Arguments[2]);
    }
}
