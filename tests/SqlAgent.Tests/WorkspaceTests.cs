using Bunit;
using Microsoft.AspNetCore.Components;
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
/// Integration coverage for the SQL tab: these exercise the real <see cref="QueryExecutionService"/>
/// (policy validation, timeout/cancellation handling, audit) over an in-memory SQLite store, the same
/// way <c>ConnectionsPageTests</c> and <c>SchemaRailTests</c> do — the brief's ResultGridTests only
/// covers the two leaf components in isolation, not the tab that wires them to the execution path.
/// </summary>
public class WorkspaceTests : IDisposable
{
    private readonly SqliteConnection _conn = new("DataSource=:memory:");
    private readonly Bunit.TestContext _ctx = new();
    private readonly WorkspaceProviderStub _providerStub = new();
    private Guid _connectionId;

    public WorkspaceTests()
    {
        _conn.Open();
        _ctx.Services.AddDbContext<SqlAgentDbContext>(o => o.UseSqlite(_conn));
        _ctx.Services.AddSingleton<ISecretStore, InMemorySecretStore>();
        _ctx.Services.AddSingleton<IDatabaseProvider>(_providerStub);
        _ctx.Services.AddSingleton<IDatabaseProviderRegistry, DatabaseProviderRegistry>();
        _ctx.Services.AddScoped<DatabaseConnectionService>();
        _ctx.Services.AddScoped<QueryExecutionService>();
        _ctx.Services.AddScoped<ScopedRunner>();
        _ctx.Services.AddScoped<AppState>();

        using var scope = _ctx.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<SqlAgentDbContext>().Database.EnsureCreated();

        // SqlEditor calls sqlAgentEditor.create on first render (see SqlEditor.razor). bUnit's JSInterop
        // defaults to strict mode, so every test that renders the SQL tab needs this planned. The `_ =>
        // true` matcher accepts any arguments — an exact-argument match would be coupled to the
        // ElementReference bUnit assigns internally, which a test can't predict. setValue is planned too:
        // it fires whenever the parent pushes a Value the editor didn't just report itself (see
        // OnAfterRenderAsync's _lastPushed check) — TypeSqlAsync below keeps _lastPushed in sync so it
        // isn't expected to fire in these tests, but planning it keeps a future test free to exercise
        // "open in editor"-style updates. destroy is planned too: SqlEditor.DisposeAsync calls it, and
        // _ctx.Dispose() (in this class's own Dispose, below) tears every rendered component down.
        _ctx.JSInterop.SetupVoid("sqlAgentEditor.create", _ => true);
        _ctx.JSInterop.SetupVoid("sqlAgentEditor.setValue", _ => true);
        _ctx.JSInterop.SetupVoid("sqlAgentEditor.destroy", _ => true);
    }

    // --- gaps the brief's ResultGridTests leaves uncovered -----------------------------------------

    [Fact]
    public void No_connection_selected_shows_a_prompt_and_no_editor()
    {
        // AppState.Connection is never set in this test: the "empty selection" branch.
        var page = _ctx.RenderComponent<Workspace>();

        Assert.Contains("Select a connection to start querying.", page.Markup);
        Assert.Empty(page.FindComponents<SqlEditor>());
    }

    [Fact]
    public async Task Selecting_a_connection_after_the_workspace_is_already_rendered_updates_it_without_further_interaction()
    {
        // SchemaRail and Workspace are siblings under MainLayout, not parent and child, so a
        // connection picked from the rail's dropdown re-renders only the rail's own subtree unless
        // Workspace itself listens for AppState.Changed. Every other test in this file calls
        // SelectConnectionAsync BEFORE rendering Workspace, so Workspace always observes the
        // connection on its own first render regardless of whether it subscribes — that blind spot
        // is exactly why this went unnoticed. Here the order is deliberately reversed, and nothing
        // else happens afterwards (no button click, no tab switch) that could force a render some
        // other way.
        var page = _ctx.RenderComponent<Workspace>();
        Assert.Contains("Select a connection to start querying.", page.Markup);

        using var scope = _ctx.Services.CreateScope();
        var connections = scope.ServiceProvider.GetRequiredService<DatabaseConnectionService>();
        var created = await connections.CreateAsync(
            new DatabaseConnectionInput("c", DatabaseProviderType.Postgres, true), "cs");
        _ctx.Services.GetRequiredService<AppState>().Select(created);

        Assert.DoesNotContain("Select a connection to start querying.", page.Markup);
    }

    [Fact]
    public async Task Rendering_the_SQL_tab_with_a_connection_selected_creates_the_CodeMirror_editor()
    {
        // The create/setValue setups in the constructor only make bUnit's strict-mode JSInterop *allow*
        // these calls; they don't prove the calls actually happen. Without this VerifyInvoke, a
        // regression that dropped the JS.InvokeVoidAsync("sqlAgentEditor.create", ...) call entirely
        // (or passed the wrong initial value) would leave every other test in this file green.
        await SelectConnectionAsync(isReadOnly: true);

        _ctx.RenderComponent<Workspace>();

        var invocation = _ctx.JSInterop.VerifyInvoke("sqlAgentEditor.create");
        // Arguments are (ElementReference host, DotNetObjectReference<SqlEditor> self, string initialValue)
        // — see SqlEditor.OnAfterRenderAsync. _sql starts as "", so that's what should reach the editor.
        Assert.Equal("", invocation.Arguments[2]);
    }

    [Fact]
    public async Task A_denied_query_shows_the_deny_code_and_reason_without_touching_the_provider()
    {
        await SelectConnectionAsync(isReadOnly: true);
        var page = _ctx.RenderComponent<Workspace>();

        await TypeSqlAsync(page, "UPDATE orders SET total = 0");
        await ClickAsync(FindButton(page, "Run"));

        Assert.Contains("policy_denied_readonly", page.Markup);
        Assert.Contains("read-only", page.Markup);
        // The provider must never be reached for SQL the policy already denied.
        Assert.False(_providerStub.WasCalled);
    }

    [Fact]
    public async Task A_truncated_result_is_announced_end_to_end()
    {
        await SelectConnectionAsync(isReadOnly: true);
        _providerStub.NextResult = new QueryResultSet(["id"], [new object?[] { 1 }], Truncated: true);

        var page = _ctx.RenderComponent<Workspace>();
        await TypeSqlAsync(page, "SELECT id FROM orders");
        await ClickAsync(FindButton(page, "Run"));

        Assert.Contains("truncated", page.Markup, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Whitespace_only_sql_keeps_the_Run_button_disabled()
    {
        await SelectConnectionAsync(isReadOnly: true);
        var page = _ctx.RenderComponent<Workspace>();

        await TypeSqlAsync(page, "   ");

        Assert.True(FindButton(page, "Run").HasAttribute("disabled"));
    }

    [Fact]
    public async Task A_successful_query_fills_the_grid_and_the_old_result_is_gone_while_the_next_run_is_in_flight()
    {
        await SelectConnectionAsync(isReadOnly: true);
        _providerStub.NextResult = new QueryResultSet(["id"], [new object?[] { 1 }], Truncated: false);

        var page = _ctx.RenderComponent<Workspace>();
        await TypeSqlAsync(page, "SELECT id FROM orders");
        await ClickAsync(FindButton(page, "Run"));

        Assert.Contains("1 rows", page.Markup);

        // The second half of the name used to be a claim, not a test. Blocking the *second* provider
        // call (the stub's counter is cumulative, so CallsToBlock = 2 lets call #1 through and
        // suspends call #2) holds the component at RunAsync's await point, which is the only moment
        // where "cleared" is observable: RunAsync sets _result = null before awaiting, so a stale grid
        // must not still be sitting there looking like the answer to the query now running.
        _providerStub.CallsToBlock = 2;
        var secondRun = ClickAsync(FindButton(page, "Run"));
        await WaitForConditionAsync(() => page.FindAll("button").Any(b => b.TextContent == "Cancel"));

        Assert.DoesNotContain("1 rows", page.Markup);

        await ClickAsync(FindButton(page, "Cancel"));
        await secondRun;
    }

    // --- the cancel path -----------------------------------------------------------------------
    //
    // ExecuteSqlAsync tells a caller-cancel apart from a timeout by which token tripped (see
    // QueryExecutionService.ExecuteSqlAsync). The WPF client never offered cancellation, so there is
    // no prior test to imitate. This is tested honestly, not mirrored from the implementation: the
    // provider stub blocks on the real cancellation token via Task.Delay(Timeout.Infinite, ct) — the
    // same technique QueryExecutionServiceTests already uses for its own timeout/cancel tests — so the
    // component genuinely races a pending query against a user's Cancel click, rather than the test
    // asserting on internal state the way the implementation does.
    //
    // Driving this requires real concurrency: bUnit's Click()/ClickAsync() dispatch an event handler
    // through the renderer's Task-based Dispatcher, and the Task that ClickAsync() returns represents
    // the *whole* handler, including any await it suspends on — not just the synchronous prefix. So
    // `await FindButton(page, "Run").ClickAsync()` would never return until the query settles, and
    // since nothing else could run on the (single) test thread while it's awaited, clicking Cancel
    // afterwards would deadlock. Starting the Task without awaiting it immediately, then awaiting a
    // non-blocking poll for the render Blazor performs when RunAsync hits its await point, sidesteps
    // that. bUnit ships a built-in RenderedFragmentWaitForHelperExtensions.WaitForStateAsync for exactly
    // this — but bunit 1.40.0 defines that type in *both* Bunit.Core.dll and Bunit.Web.dll, and since the
    // `bunit` meta-package references both, the extension-method lookup is ambiguous and the compiler
    // drops it from candidates entirely (confirmed with a throwaway diagnostic file: calling the type
    // name directly gives CS0433 "exists in both ... Bunit.Core ... and ... Bunit.Web", while the
    // extension-method call site just reports CS1061 "not found"). Rather than fight the package's
    // internals with an extern alias, WaitForConditionAsync below is a minimal local stand-in — same
    // non-blocking polling idea, no dependency on the ambiguous type.

    [Fact]
    public async Task Cancel_reports_execution_canceled_and_the_Run_button_works_again()
    {
        await SelectConnectionAsync(isReadOnly: true);
        _providerStub.CallsToBlock = 1;

        var page = _ctx.RenderComponent<Workspace>();
        await TypeSqlAsync(page, "SELECT id FROM orders");

        var runClick = ClickAsync(FindButton(page, "Run"));
        await WaitForConditionAsync(() => page.FindAll("button").Any(b => b.TextContent == "Cancel"));

        // The Run button is disabled while the query is in flight — the UI's own guard against a
        // second overlapping execution.
        Assert.True(FindButton(page, "Run").HasAttribute("disabled"));

        await ClickAsync(FindButton(page, "Cancel"));
        await runClick;

        Assert.Contains("execution_canceled", page.Markup);
        Assert.Contains("Query was canceled.", page.Markup);

        // _running must not be stuck true: the Cancel button is gone and Run is clickable again.
        Assert.DoesNotContain(page.FindAll("button"), b => b.TextContent == "Cancel");
        Assert.False(FindButton(page, "Run").HasAttribute("disabled"));
    }

    // --- guarding against a stale result --------------------------------------------------------
    //
    // Scenario from the task brief: run, cancel, run again. The component must show only the second
    // outcome — never let the first (canceled) call's late-arriving continuation overwrite it — and
    // _running must genuinely be available for a second run, not just appear so.
    //
    // Why this is safe by construction rather than by luck: RunAsync only ever assigns to _result and
    // _running from within its own async state machine, and the Run button that starts a *second*
    // RunAsync is disabled for the entire lifetime of the first (disabled="@(_running || ...)"), so a
    // second invocation cannot start until the first's `finally` (which sets _running = false) has
    // already run to completion. There is no interleaving where the first call's result assignment
    // could land after the second's — they are strictly sequential, never concurrent. This test proves
    // that ordering holds in practice, not just on paper.

    [Fact]
    public async Task A_second_run_after_cancelling_the_first_shows_only_the_second_result()
    {
        await SelectConnectionAsync(isReadOnly: true);
        _providerStub.CallsToBlock = 1;
        _providerStub.NextResult = new QueryResultSet(["id"], [new object?[] { 42 }], Truncated: false);

        var page = _ctx.RenderComponent<Workspace>();
        await TypeSqlAsync(page, "SELECT id FROM orders");

        var firstRun = ClickAsync(FindButton(page, "Run"));
        await WaitForConditionAsync(() => page.FindAll("button").Any(b => b.TextContent == "Cancel"));
        await ClickAsync(FindButton(page, "Cancel"));
        await firstRun;

        Assert.Contains("execution_canceled", page.Markup);

        // Second run: the stub no longer blocks (CallsToBlock was consumed), so this completes
        // immediately and must fully replace the first outcome.
        await ClickAsync(FindButton(page, "Run"));

        Assert.DoesNotContain("execution_canceled", page.Markup);
        Assert.Contains("1 rows", page.Markup);
        Assert.DoesNotContain(page.FindAll("button"), b => b.TextContent == "Cancel");
        Assert.False(FindButton(page, "Run").HasAttribute("disabled"));
    }

    // --- Ctrl+Enter must carry the same guards the Run button carries -------------------------
    //
    // Before this task the Run button's disabled="@(_running || string.IsNullOrWhiteSpace(_sql))"
    // attribute was the *only* way into RunAsync, so RunAsync itself never needed to re-check either
    // condition — the button made both states unreachable. SqlEditor's Ctrl+Enter (OnRun) is a second
    // entry point that calls RunAsync directly, bypassing the button's disabled attribute entirely, so
    // RunAsync must guard itself now. These two tests were red against the unguarded RunAsync: the
    // whitespace test failed because Assert.DoesNotContain("policy_denied_empty", ...) found the code
    // (SqlPolicyValidator.Validate denies blank SQL, but only *after* RunAsync's body ran and set
    // _result, which the guard now prevents from happening at all), and the double-dispatch test failed
    // because _providerStub.CallCount came back 2, not 1.

    [Fact]
    public async Task Ctrl_Enter_with_whitespace_SQL_runs_nothing()
    {
        await SelectConnectionAsync(isReadOnly: true);
        var page = _ctx.RenderComponent<Workspace>();
        await TypeSqlAsync(page, "   ");

        await PressCtrlEnterAsync(page);

        Assert.False(_providerStub.WasCalled);
        // Not just "no rows shown" — no execution attempt at all, so no deny message either. If RunAsync
        // let this through, ExecuteSqlAsync would deny it as policy_denied_empty and that code would show
        // up in the markup even though the provider itself was still never reached.
        Assert.DoesNotContain("policy_denied_empty", page.Markup);
    }

    [Fact]
    public async Task A_second_Ctrl_Enter_while_a_query_is_in_flight_does_not_start_a_second_execution()
    {
        await SelectConnectionAsync(isReadOnly: true);
        _providerStub.CallsToBlock = 1;

        var page = _ctx.RenderComponent<Workspace>();
        await TypeSqlAsync(page, "SELECT id FROM orders");

        var firstRun = PressCtrlEnterAsync(page);
        await WaitForConditionAsync(() => page.FindAll("button").Any(b => b.TextContent == "Cancel"));

        // Reachable via plain OS key repeat while a query is in flight. Must be a no-op: only one
        // execution should ever reach the provider, and the first call's _cts must not be reassigned or
        // disposed by a second, unguarded RunAsync invocation.
        await PressCtrlEnterAsync(page);

        Assert.Equal(1, _providerStub.CallCount);

        // Prove the first run's own cancellation still works cleanly — if the second Ctrl+Enter had
        // reassigned _cts, Cancel here would target the wrong (or an already-disposed) token source.
        await ClickAsync(FindButton(page, "Cancel"));
        await firstRun;

        Assert.Contains("execution_canceled", page.Markup);
        Assert.False(FindButton(page, "Run").HasAttribute("disabled"));
    }

    // WaitForConditionAsync (the bUnit WaitForStateAsync stand-in referenced above) now lives in
    // AsyncTestHelpers so SchemaRailTests can share it instead of carrying its own copy.

    private static AngleSharp.Dom.IElement FindButton(IRenderedComponent<Workspace> page, string text) =>
        page.FindAll("button").First(b => b.TextContent.Trim() == text);

    // The SQL tab no longer has a textarea to drive: CodeMirror owns the DOM node, and bUnit has no
    // JS engine to run it (see SqlEditor.razor / sql-editor.js). SqlEditor.OnEditorChanged is the exact
    // [JSInvokable] the browser calls on every keystroke (wired up in sql-editor.js's 'change' handler),
    // so invoking it directly on the component instance drives the identical code path a real keystroke
    // would — it updates Value and raises ValueChanged, same as if CodeMirror had called it. The call is
    // routed through the renderer via InvokeAsync so the ValueChanged-triggered re-render of Workspace
    // happens on the correct synchronization context, the same way ClickAsync does for button clicks.
    private static Task TypeSqlAsync(IRenderedComponent<Workspace> page, string sql) =>
        page.InvokeAsync(() => page.FindComponent<SqlEditor>().Instance.OnEditorChanged(sql));

    // Mirrors TypeSqlAsync: SqlEditor.RunFromEditor is the exact [JSInvokable] sql-editor.js's
    // 'Ctrl-Enter'/'Cmd-Enter' extraKeys call, so invoking it directly drives the identical path a real
    // Ctrl+Enter keystroke would, without needing a JS engine to press the key in a live editor.
    private static Task PressCtrlEnterAsync(IRenderedComponent<Workspace> page) =>
        page.InvokeAsync(() => page.FindComponent<SqlEditor>().Instance.RunFromEditor());

    private static Task ClickAsync(AngleSharp.Dom.IElement button) =>
        button.ClickAsync(new MouseEventArgs());

    private async Task SelectConnectionAsync(bool isReadOnly)
    {
        using var scope = _ctx.Services.CreateScope();
        var connections = scope.ServiceProvider.GetRequiredService<DatabaseConnectionService>();
        var created = await connections.CreateAsync(
            new DatabaseConnectionInput("c", DatabaseProviderType.Postgres, isReadOnly), "cs");
        _connectionId = created.Id;
        _ctx.Services.GetRequiredService<AppState>().Select(created);
    }

    public void Dispose()
    {
        _ctx.Dispose();
        _conn.Dispose();
    }
}

/// <summary>
/// Provider double for the SQL tab. Most tests just need <see cref="NextResult"/> returned
/// immediately. The cancel and stale-result tests set <see cref="CallsToBlock"/> so the first call(s)
/// genuinely suspend on the caller's token via Task.Delay(Timeout.Infinite, ct) — the same technique
/// QueryExecutionServiceTests uses — instead of completing synchronously, so cancellation is exercised
/// for real rather than simulated by asserting on internal state.
/// </summary>
sealed class WorkspaceProviderStub : IDatabaseProvider
{
    public bool WasCalled { get; private set; }
    public int CallsToBlock { get; set; }
    public QueryResultSet NextResult { get; set; } = new([], [], false);

    /// <summary>Total number of times <see cref="ExecuteQueryAsync"/> was actually invoked.</summary>
    public int CallCount => _calls;

    private int _calls;

    public DatabaseProviderType ProviderType => DatabaseProviderType.Postgres;

    public Task<ConnectionTestResult> TestConnectionAsync(string cs, CancellationToken ct = default)
        => Task.FromResult(ConnectionTestResult.Ok(null, 0));

    public Task<DatabaseSchema> GetSchemaAsync(string cs, CancellationToken ct = default)
        => Task.FromResult(new DatabaseSchema([]));

    public async Task<QueryResultSet> ExecuteQueryAsync(
        string cs, string sql, QueryExecutionOptions o, CancellationToken ct = default)
    {
        WasCalled = true;
        if (Interlocked.Increment(ref _calls) <= CallsToBlock)
            await Task.Delay(Timeout.Infinite, ct);
        return NextResult;
    }
}
