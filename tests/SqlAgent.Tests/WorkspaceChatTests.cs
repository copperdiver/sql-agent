using Bunit;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SqlAgent.Core;
using SqlAgent.Host.Components.Pages;
using SqlAgent.Host.Components.Shared;
using SqlAgent.Host.Web;
using SqlAgent.Storage;
using static SqlAgent.Tests.AsyncTestHelpers;

namespace SqlAgent.Tests;

/// <summary>
/// The three ask_database outcomes render differently, and llm_error is special: with no provider wired
/// it is the only outcome the user will ever see, so it must read as "not configured" rather than as a
/// failure of their question.
///
/// The first four tests below (from the task brief) exercise <see cref="ChatOutcome"/> in isolation, the
/// same way the brief's own <c>ResultGridTests</c> exercises the SQL tab's leaf component. The rest wire
/// the chat tab into <see cref="Workspace"/> end to end the way <see cref="WorkspaceTests"/> does for the
/// SQL tab, covering branches the brief's test list does not: no connection selected, an empty/whitespace
/// question, a second question arriving while the first is still in flight, and switching tabs mid-request.
/// </summary>
public class WorkspaceChatTests : IDisposable
{
    private readonly SqliteConnection _conn = new("DataSource=:memory:");
    private readonly Bunit.TestContext _ctx = new();
    private readonly WorkspaceProviderStub _providerStub = new();
    private readonly ChatGatewayStub _gatewayStub = new();

    public WorkspaceChatTests()
    {
        _conn.Open();
        _ctx.Services.AddDbContext<SqlAgentDbContext>(o => o.UseSqlite(_conn));
        _ctx.Services.AddSingleton<ISecretStore, InMemorySecretStore>();
        _ctx.Services.AddSingleton<IDatabaseProvider>(_providerStub);
        _ctx.Services.AddSingleton<IDatabaseProviderRegistry, DatabaseProviderRegistry>();
        _ctx.Services.AddSingleton<ILlmSqlGateway>(_gatewayStub);
        _ctx.Services.AddScoped<DatabaseConnectionService>();
        _ctx.Services.AddScoped<QueryExecutionService>();
        _ctx.Services.AddScoped<SchemaService>();
        _ctx.Services.AddScoped<NlQueryService>();
        _ctx.Services.AddScoped<ScopedRunner>();
        _ctx.Services.AddScoped<AppState>();

        using var scope = _ctx.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<SqlAgentDbContext>().Database.EnsureCreated();

        // Switching to the SQL tab (directly, or via "open in editor") mounts SqlEditor, which needs
        // strict-mode bUnit JSInterop planned exactly as WorkspaceTests does. See the comment there for
        // why all three calls are planned even though not every test triggers all of them.
        _ctx.JSInterop.SetupVoid("sqlAgentEditor.create", _ => true);
        _ctx.JSInterop.SetupVoid("sqlAgentEditor.setValue", _ => true);
        _ctx.JSInterop.SetupVoid("sqlAgentEditor.destroy", _ => true);
    }

    // --- the brief's four ChatOutcome tests, verbatim ------------------------------------------

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

    // --- gaps the brief's test list leaves uncovered --------------------------------------------
    //
    // Every review of this plan so far has found the brief's test list under-covering real branches.
    // These exercise the chat tab wired into Workspace end to end (the same real NlQueryService, over an
    // in-memory SQLite store and provider/gateway doubles, the way WorkspaceTests does for the SQL tab):
    // no connection selected, a whitespace-only question, two questions in a row staying correctly
    // paired, a second question arriving while the first is still in flight, and a tab switch mid-request.

    [Fact]
    public async Task Selecting_the_Chat_tab_with_no_connection_selected_shows_the_prompt_not_the_transcript()
    {
        // AppState.Connection is never set: the Chat tab button is reachable, but the outer
        // State.ConnectionId==null branch in Workspace must win over _tab, exactly as it does for SQL.
        var page = _ctx.RenderComponent<Workspace>();

        await ClickAsync(FindButton(page, "Chat"));

        Assert.Contains("Select a connection to start querying.", page.Markup);
        Assert.DoesNotContain("Ask a question about this database", page.Markup);
    }

    [Fact]
    public async Task Whitespace_only_question_keeps_the_Ask_button_disabled()
    {
        await SelectConnectionAsync();
        var page = _ctx.RenderComponent<Workspace>();
        await ClickAsync(FindButton(page, "Chat"));

        TypeQuestion(page, "   ");

        Assert.True(FindButton(page, "Ask").HasAttribute("disabled"));
    }

    [Fact]
    public async Task Two_sequential_questions_each_keep_their_own_question_paired_with_their_own_result()
    {
        await SelectConnectionAsync();
        var page = _ctx.RenderComponent<Workspace>();
        await ClickAsync(FindButton(page, "Chat"));

        _gatewayStub.NextResponse = LlmSqlResponse.Clarify("Which year?");
        TypeQuestion(page, "how many orders");
        await ClickAsync(FindButton(page, "Ask"));

        _gatewayStub.NextResponse = LlmSqlResponse.Generated("SELECT id FROM orders");
        _providerStub.NextResult = new QueryResultSet(["id"], [new object?[] { 7 }], false);
        TypeQuestion(page, "orders in 2024");
        await ClickAsync(FindButton(page, "Ask"));

        // Both entries must be present with their own outcome — neither the question text nor the
        // outcome of the first was overwritten by the second.
        Assert.Contains("how many orders", page.Markup);
        Assert.Contains("Which year?", page.Markup);
        Assert.Contains("orders in 2024", page.Markup);
        Assert.Contains("SELECT id FROM orders", page.Markup);
    }

    // --- guarding against overlapping asks ------------------------------------------------------
    //
    // Scenario the task description calls out by name: "an in-flight question must not be able to
    // attach its result to the wrong entry if the user asks again quickly". AskAsync captures its own
    // `question` and `result` into locals and pairs them in one TranscriptEntry, so a completed call can
    // never attach to the wrong entry by itself — but without a re-entrancy guard, a second overlapping
    // call is still harmful: _asking is a single shared field, so if a second (e.g. fast-completing,
    // empty-question) call's `finally` ran before the first genuine call finished, it would reset
    // _asking = false while the first was still in flight, re-enabling the Ask button early and letting
    // a third question start genuinely concurrently with the first. Guarding re-entry the way RunAsync
    // does closes that off entirely: overlapping calls are impossible, not just made harmless by luck.

    [Fact]
    public async Task A_second_Ask_click_while_the_first_is_in_flight_is_ignored()
    {
        await SelectConnectionAsync();
        _gatewayStub.Hold();

        var page = _ctx.RenderComponent<Workspace>();
        await ClickAsync(FindButton(page, "Chat"));
        TypeQuestion(page, "first question");

        var firstAsk = ClickAsync(FindButton(page, "Ask"));
        await WaitForConditionAsync(() => _gatewayStub.CallCount == 1);

        // The Ask button is disabled while the first question is in flight.
        Assert.True(FindButton(page, "Ask").HasAttribute("disabled"));

        // The user types a second question and tries to submit it while the first is still pending.
        // This must be a no-op: no second call to the gateway, and _asking must not be reset early.
        TypeQuestion(page, "second question");
        await ClickAsync(FindButton(page, "Ask"));

        Assert.Equal(1, _gatewayStub.CallCount);
        Assert.True(FindButton(page, "Ask").HasAttribute("disabled"));

        _gatewayStub.Release(LlmSqlResponse.Generated("SELECT 1"));
        await firstAsk;

        // Exactly one transcript entry, for the first question only — the guarded second click added
        // nothing, and the first's result landed on the first's own question. Scoped to the transcript's
        // own question paragraphs (not the whole page) because "second question" is legitimately still
        // sitting in the input's value attribute at this point — see below.
        var transcriptQuestions = page.FindAll("p.question").Select(p => p.TextContent).ToList();
        Assert.Contains("first question", transcriptQuestions);
        Assert.DoesNotContain("second question", transcriptQuestions);
        Assert.False(FindButton(page, "Ask").HasAttribute("disabled"));

        // The second question is still sitting in the input, unsent rather than silently dropped: it
        // can be submitted for real now that the guard has cleared.
        await ClickAsync(FindButton(page, "Ask"));
        Assert.Equal(2, _gatewayStub.CallCount);
        transcriptQuestions = page.FindAll("p.question").Select(p => p.TextContent).ToList();
        Assert.Contains("second question", transcriptQuestions);
    }

    [Fact]
    public async Task Switching_to_the_SQL_tab_while_a_question_is_in_flight_does_not_lose_the_answer()
    {
        // SqlEditor is unmounted/remounted across tab switches (see SqlEditor's teardown and
        // OnAfterRenderAsync), but Workspace itself stays mounted the whole time, so the AskAsync
        // coroutine started on the Chat tab must keep running and land its result in _transcript even
        // while the SQL tab is what's actually on screen.
        await SelectConnectionAsync();
        _gatewayStub.Hold();
        _gatewayStub.NextResponse = LlmSqlResponse.Generated("SELECT id FROM orders");
        _providerStub.NextResult = new QueryResultSet(["id"], [new object?[] { 1 }], false);

        var page = _ctx.RenderComponent<Workspace>();
        await ClickAsync(FindButton(page, "Chat"));
        TypeQuestion(page, "how many orders");

        var ask = ClickAsync(FindButton(page, "Ask"));
        await WaitForConditionAsync(() => _gatewayStub.CallCount == 1);

        // Switch away while the question is still pending.
        await ClickAsync(FindButton(page, "SQL"));
        Assert.DoesNotContain("Ask a question about this database", page.Markup);

        _gatewayStub.Release(_gatewayStub.NextResponse!);
        await ask;

        // No exception reached the ErrorBoundary, and switching back to Chat shows the completed answer.
        Assert.DoesNotContain("Something went wrong", page.Markup);
        await ClickAsync(FindButton(page, "Chat"));

        Assert.Contains("how many orders", page.Markup);
        Assert.Contains("SELECT id FROM orders", page.Markup);
    }

    // --- "open in editor" must not lose the generated SQL ---------------------------------------
    //
    // The SQL tab's editor is unmounted while the Chat tab is showing (Workspace's @if/else if means
    // only one of SqlEditor/the chat transcript exists in the render tree at a time). So clicking
    // "open in editor" cannot push text into an already-mounted CodeMirror instance via setValue — there
    // isn't one. Instead it sets _sql and flips _tab, and the *next* render constructs a brand new
    // SqlEditor, which pushes _sql through as its initial value on first render (see SqlEditor's
    // OnAfterRenderAsync: firstRender always calls sqlAgentEditor.create with Value, and setValue is
    // reserved for pushing into an editor that was already mounted). Pinning this matters because a user
    // who clicks "open in editor" and finds an empty editor has lost the model's generated SQL.

    [Fact]
    public async Task Open_in_editor_switches_to_the_SQL_tab_and_seeds_a_freshly_mounted_editor_with_the_generated_sql()
    {
        await SelectConnectionAsync();
        _gatewayStub.NextResponse = LlmSqlResponse.Generated("SELECT id FROM orders");
        _providerStub.NextResult = new QueryResultSet(["id"], [new object?[] { 1 }], false);

        var page = _ctx.RenderComponent<Workspace>();
        await ClickAsync(FindButton(page, "Chat"));
        TypeQuestion(page, "show me orders");
        await ClickAsync(FindButton(page, "Ask"));

        Assert.Contains("SELECT id FROM orders", page.Markup);

        await ClickAsync(FindButton(page, "Open in editor"));

        // The chat tab is gone; the SQL tab (and its editor) is what's on screen now.
        Assert.DoesNotContain("Ask a question about this database", page.Markup);

        // Two "create" calls total are expected here, not one: Workspace's default tab is Sql, so the
        // very first render already mounts an (empty) editor before we ever switch to Chat; switching to
        // Chat destroys it, and "open in editor" triggers the second, freshly-mounted create call this
        // test cares about. Assert on that last call specifically.
        var createCalls = _ctx.JSInterop.Invocations.Where(i => i.Identifier == "sqlAgentEditor.create").ToList();
        Assert.Equal(2, createCalls.Count);
        Assert.Equal("SELECT id FROM orders", createCalls[^1].Arguments[2]);

        // It must have arrived as the fresh editor's initial value, never as a push into an editor that
        // was already on screen — there wasn't one to push into.
        Assert.DoesNotContain(_ctx.JSInterop.Invocations, i => i.Identifier == "sqlAgentEditor.setValue");
    }

    // bUnit's InputAsync only accepts a ChangeEventArgs, not a bare string (unlike ClickAsync, which has
    // a string-friendly overload via MouseEventArgs); the synchronous Input(string) overload does accept
    // one, and the @bind:event="oninput" handler it triggers here is synchronous, so no await is needed.
    private static void TypeQuestion(IRenderedComponent<Workspace> page, string text) =>
        page.Find("input[placeholder='Ask a question about this database']").Input(text);

    private static AngleSharp.Dom.IElement FindButton(IRenderedComponent<Workspace> page, string text) =>
        page.FindAll("button").First(b => b.TextContent.Trim() == text);

    private static Task ClickAsync(AngleSharp.Dom.IElement button) =>
        button.ClickAsync(new MouseEventArgs());

    private async Task SelectConnectionAsync()
    {
        using var scope = _ctx.Services.CreateScope();
        var connections = scope.ServiceProvider.GetRequiredService<DatabaseConnectionService>();
        var created = await connections.CreateAsync(
            new DatabaseConnectionInput("c", DatabaseProviderType.Postgres, IsReadOnly: true), "cs");
        _ctx.Services.GetRequiredService<AppState>().Select(created);
    }

    public void Dispose()
    {
        _ctx.Dispose();
        _conn.Dispose();
    }
}

/// <summary>
/// LLM gateway double for the chat tab's integration tests. Most tests just want an immediate canned
/// response or a thrown exception (mirrors <c>NlQueryServiceTests</c>' FakeGateway). The in-flight/guard
/// tests need genuine control over when <see cref="GenerateSqlAsync"/> resumes — unlike the SQL tab's
/// provider stub (which blocks on a real CancellationToken it can eventually observe via the Cancel
/// button), the chat tab has no cancel action and Workspace calls NlQueryService.AskAsync without a
/// token, so blocking via Task.Delay(Timeout.Infinite, ct) would hang forever with nothing to cancel it.
/// Hold()/Release() give the test explicit, deterministic control instead.
/// </summary>
sealed class ChatGatewayStub : ILlmSqlGateway
{
    public LlmSqlResponse? NextResponse { get; set; }
    public int CallCount => _calls;

    private int _calls;
    private TaskCompletionSource<LlmSqlResponse>? _gate;

    public void Hold() => _gate = new TaskCompletionSource<LlmSqlResponse>(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Release(LlmSqlResponse response) => _gate?.SetResult(response);

    public Task<LlmSqlResponse> GenerateSqlAsync(LlmSqlRequest request, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _calls);
        if (_gate is { } gate) return gate.Task;
        return Task.FromResult(NextResponse ?? LlmSqlResponse.Generated("SELECT 1"));
    }
}
