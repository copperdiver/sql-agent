using Bunit;
using SqlAgent.Core;
using SqlAgent.Host.Components.Shared;

namespace SqlAgent.Tests;

public class ResultGridTests
{
    private static QueryExecutionResult Success(bool truncated) => QueryExecutionResult.Ok(
        "SELECT 1",
        new QueryResultSet(["id", "name"], [new object?[] { 1, "a" }, new object?[] { 2, null }], truncated),
        18);

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
}
