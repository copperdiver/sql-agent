using Bunit;
using SqlAgent.Host.Components.Shared;
using SqlAgent.Storage;

namespace SqlAgent.Tests;

/// <summary>
/// ChatOutcome in isolation. The tab that used to wrap it is gone — Phase B1 moved conversations to
/// ChatPage.razor and the SQL editor to /sql — but the component itself survives until Phase D replaces it
/// with SqlBlock and DataTable, and these five outcomes are exactly what it has to keep rendering.
/// </summary>
public class ChatOutcomeTests
{
    [Fact]
    public void An_llm_not_configured_error_is_explained_rather_than_shown_as_a_raw_code()
    {
        // The brief's original version of this test constructed an "llm_error"-coded result, because
        // today UnavailableLlmSqlGateway is the only gateway and every failure comes out as llm_error.
        // That conflated two different meanings: "no provider is configured" and "the provider call
        // failed". NlQueryService now gives the former its own code (llm_not_configured) so llm_error
        // stays free for genuine provider failures once a real one is wired — see the sibling test below.
        using var ctx = new Bunit.TestContext();
        var result = NlQueryResult.Error("llm_not_configured", "No LLM provider is configured on this server.");

        var view = ctx.RenderComponent<ChatOutcome>(p => p.Add(c => c.Result, result));

        Assert.Contains("LLM is not configured", view.Markup);
        Assert.DoesNotContain("llm_not_configured", view.Markup);
    }

    [Fact]
    public void A_genuine_llm_error_does_not_get_the_not_configured_explanation()
    {
        // A configured provider that fails for its own reasons (timeout, network error, malformed
        // response) still comes back as llm_error. It must fall through to the ordinary code-and-message
        // rendering, not be mistaken for "no provider configured" — telling the user the server has no
        // LLM configured when their question just failed would be actively misleading.
        using var ctx = new Bunit.TestContext();
        var result = NlQueryResult.Error("llm_error", "The language model could not process the request.");

        var view = ctx.RenderComponent<ChatOutcome>(p => p.Add(c => c.Result, result));

        Assert.DoesNotContain("not configured", view.Markup, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("llm_error", view.Markup);
        Assert.Contains("The language model could not process the request.", view.Markup);
    }

    [Fact]
    public void A_clarification_shows_the_question_and_no_sql()
    {
        using var ctx = new Bunit.TestContext();
        var result = NlQueryResult.Clarification("Which year did you mean?");

        var view = ctx.RenderComponent<ChatOutcome>(p => p.Add(c => c.Result, result));

        Assert.Contains("Which year did you mean?", view.Markup);
        Assert.DoesNotContain("<pre", view.Markup);
    }

    [Fact]
    public void A_rejected_query_still_shows_the_generated_sql()
    {
        using var ctx = new Bunit.TestContext();
        var result = NlQueryResult.Error(
            "policy_denied_hidden_table", "Query references a hidden table.", "SELECT * FROM secrets");

        var view = ctx.RenderComponent<ChatOutcome>(p => p.Add(c => c.Result, result));

        // Auditability: the user must be able to see what was generated and why it was refused.
        Assert.Contains("SELECT * FROM secrets", view.Markup);
        Assert.Contains("policy_denied_hidden_table", view.Markup);
    }

    [Fact]
    public void A_successful_answer_shows_the_generated_sql_and_the_rows()
    {
        using var ctx = new Bunit.TestContext();
        var result = new NlQueryResult(
            NlResponseKind.QueryResult, "SELECT count(*) FROM orders", null, null, null,
            ["count"], [new object?[] { 42 }], 1, false, 7);

        var view = ctx.RenderComponent<ChatOutcome>(p => p.Add(c => c.Result, result));

        Assert.Contains("SELECT count(*) FROM orders", view.Markup);
        Assert.Contains("42", view.Markup);
    }

    [Fact]
    public void A_restored_answer_shows_its_numbers_and_says_where_the_rows_went()
    {
        // A reloaded QueryResult has no rows — they are never stored. Rendering the usual table would
        // draw an empty grid, which reads as "the query returned nothing" rather than "the rows are not
        // kept". This is the only visible consequence of the rows-not-persisted rule, so it says so.
        using var ctx = new Bunit.TestContext();
        var restored = new NlQueryResult(
            NlResponseKind.QueryResult, "SELECT id FROM orders", null, null, null,
            [], [], RowCount: 214, Truncated: true, ElapsedMs: 38);

        var view = ctx.RenderComponent<ChatOutcome>(p => p
            .Add(c => c.Result, restored)
            .Add(c => c.Restored, true));

        Assert.Contains("214", view.Markup);
        Assert.Contains("38", view.Markup);
        Assert.Contains("truncated", view.Markup);
        Assert.Contains("Rows are not stored", view.Markup);
        Assert.Empty(view.FindAll("table"));
    }
}
