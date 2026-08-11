using Bunit;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SqlAgent.Core;
using SqlAgent.Host.Components.Layout;
using SqlAgent.Host.Components.Pages;
using SqlAgent.Host.Web;
using SqlAgent.Storage;
using static SqlAgent.Tests.AsyncTestHelpers;

namespace SqlAgent.Tests;

public class SchemaRailTests : IDisposable
{
    private readonly SqliteConnection _conn = new("DataSource=:memory:");
    private readonly Bunit.TestContext _ctx = new();
    private Guid _connectionId;

    public SchemaRailTests()
    {
        _conn.Open();
        _ctx.Services.AddDbContext<SqlAgentDbContext>(o => o.UseSqlite(_conn));
        _ctx.Services.AddSingleton<ISecretStore, InMemorySecretStore>();
        _ctx.Services.AddSingleton<IDatabaseProvider, RailProviderStub>();
        _ctx.Services.AddSingleton<IDatabaseProviderRegistry, DatabaseProviderRegistry>();
        _ctx.Services.AddScoped<DatabaseConnectionService>();
        _ctx.Services.AddScoped<TablePolicyService>();
        // The propagation tests below render the real Connections page alongside the rail rather than
        // poking AppState directly, because the seam under test is the one between them.
        _ctx.Services.AddScoped<ConnectionTester>();
        _ctx.Services.AddScoped<ScopedRunner>();
        _ctx.Services.AddScoped<AppState>();
        _ctx.Services.AddLogging();

        using var scope = _ctx.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<SqlAgentDbContext>().Database.EnsureCreated();
        var connections = scope.ServiceProvider.GetRequiredService<DatabaseConnectionService>();
        var created = connections.CreateAsync(
            new DatabaseConnectionInput("c", DatabaseProviderType.Postgres, true), "cs").GetAwaiter().GetResult();
        _connectionId = created.Id;
        _ctx.Services.GetRequiredService<AppState>().Select(created);
    }

    [Fact]
    public void Every_live_table_is_listed_including_hidden_ones()
    {
        HideAsync("secrets").GetAwaiter().GetResult();

        var rail = _ctx.RenderComponent<SchemaRail>();

        // The rail is a configuration surface: a hidden table must stay visible here or it could
        // never be restored. Only the schema handed to the agent is filtered.
        Assert.Contains("orders", rail.Markup);
        Assert.Contains("secrets", rail.Markup);
    }

    [Fact]
    public void A_hidden_table_is_rendered_dimmed()
    {
        HideAsync("secrets").GetAwaiter().GetResult();

        var rail = _ctx.RenderComponent<SchemaRail>();

        var row = rail.Find("[data-table='public.secrets']");
        Assert.Contains("hidden", row.ClassList);
    }

    [Fact]
    public void Toggling_the_checkbox_persists_the_new_visibility()
    {
        var rail = _ctx.RenderComponent<SchemaRail>();

        rail.Find("[data-table='public.secrets'] input[type=checkbox]").Change(false);

        using var scope = _ctx.Services.CreateScope();
        var policies = scope.ServiceProvider.GetRequiredService<TablePolicyService>();
        var listed = policies.ListAsync(_connectionId).GetAwaiter().GetResult()!;
        Assert.False(listed.Single(t => t.Table == "secrets").IsVisible);
    }

    [Fact]
    public void Search_filters_the_tree_by_table_name()
    {
        var rail = _ctx.RenderComponent<SchemaRail>();

        rail.Find("input[type=search]").Change("secr");

        Assert.DoesNotContain("orders", rail.Markup);
        Assert.Contains("secrets", rail.Markup);
    }

    [Fact]
    public void Switching_the_connection_reloads_the_table_list_for_the_new_connection()
    {
        Guid secondId;
        using (var scope = _ctx.Services.CreateScope())
        {
            var connections = scope.ServiceProvider.GetRequiredService<DatabaseConnectionService>();
            var second = connections.CreateAsync(
                new DatabaseConnectionInput("c2", DatabaseProviderType.Postgres, true), "cs2")
                .GetAwaiter().GetResult();
            secondId = second.Id;
            var policies = scope.ServiceProvider.GetRequiredService<TablePolicyService>();
            // Hide "orders" only for the second connection, so the two connections' table lists
            // are distinguishable and a stale (first-connection) list would fail the assertion below.
            policies.SetVisibilityAsync(secondId, "public", "orders", false).GetAwaiter().GetResult();
        }

        var rail = _ctx.RenderComponent<SchemaRail>();
        Assert.DoesNotContain("hidden", rail.Find("[data-table='public.orders']").ClassList);

        rail.Find("select").Change(secondId.ToString());

        Assert.Contains("hidden", rail.Find("[data-table='public.orders']").ClassList);
    }

    [Fact]
    public void Selecting_no_connection_clears_the_schema_tree()
    {
        var rail = _ctx.RenderComponent<SchemaRail>();
        Assert.Contains("orders", rail.Markup); // sanity: a connection is selected initially

        rail.Find("select").Change("");

        // No connection selected: the whole schema section (search box and tree) must disappear,
        // not just render empty — there is nothing to filter or toggle.
        Assert.DoesNotContain("orders", rail.Markup);
        Assert.DoesNotContain("secrets", rail.Markup);
    }

    [Fact]
    public void A_connection_with_a_missing_secret_shows_an_empty_tree_without_crashing()
    {
        RemoveStoredSecretAsync(_connectionId).GetAwaiter().GetResult();

        // TablePolicyService.ListAsync returns null once the secret can no longer be resolved; the
        // rail must fall back to an empty table list rather than propagate that null into rendering.
        var rail = _ctx.RenderComponent<SchemaRail>();

        Assert.DoesNotContain("orders", rail.Markup);
        Assert.DoesNotContain("secrets", rail.Markup);
    }

    // --- the post-mount blind spot ----------------------------------------------------------------
    //
    // Every test above creates its connections in the constructor, before RenderComponent<SchemaRail>,
    // so the rail always observes them on its first render whether or not it ever listens for a later
    // change. That is the same shape as the Task 10 Critical (Workspace never subscribing to
    // AppState.Changed) and it hid the same bug twice: the rail reads its picker list once in
    // OnInitializedAsync and then lives in MainLayout for the whole circuit, which no route
    // navigation recreates. These three drive the real Connections page after the rail is already on
    // screen, which is the only order in which the propagation exists at all.

    [Fact]
    public async Task A_connection_created_after_the_rail_is_rendered_appears_in_its_picker()
    {
        var rail = _ctx.RenderComponent<SchemaRail>();
        Assert.DoesNotContain("added-later", rail.Markup);

        var page = _ctx.RenderComponent<Connections>();
        page.Find("input").Change("added-later");
        page.Find("input[type=password]").Change("cs2");
        FindButton(page, "Save").Click();

        await WaitForConditionAsync(() => rail.Markup.Contains("added-later"));
    }

    [Fact]
    public async Task A_connection_deleted_after_the_rail_is_rendered_stops_being_offered()
    {
        var rail = _ctx.RenderComponent<SchemaRail>();
        Assert.NotEmpty(rail.FindAll($"option[value='{_connectionId}']"));

        var page = _ctx.RenderComponent<Connections>();
        FindButton(page, "Delete").Click();

        await WaitForConditionAsync(() => rail.FindAll($"option[value='{_connectionId}']").Count == 0);
    }

    [Fact]
    public async Task Editing_the_already_selected_connection_updates_what_the_rail_shows_about_it()
    {
        var rail = _ctx.RenderComponent<SchemaRail>();
        Assert.Contains("read-only", rail.Markup);

        var page = _ctx.RenderComponent<Connections>();
        FindButton(page, "Edit").Click();
        page.Find("input[type=checkbox]").Change(false);     // the Read-only checkbox
        FindButton(page, "Save").Click();

        // The rail's meta line reads AppState.Connection, not its own list. AppState.Select used to
        // early-return on an unchanged id, so editing the row that was already selected left the rail
        // describing the connection as it was before the edit, forever.
        await WaitForConditionAsync(() => rail.Markup.Contains("read-write"));
    }

    [Fact]
    public void Switching_the_connection_clears_the_table_filter()
    {
        Guid secondId;
        using (var scope = _ctx.Services.CreateScope())
        {
            var connections = scope.ServiceProvider.GetRequiredService<DatabaseConnectionService>();
            secondId = connections.CreateAsync(
                new DatabaseConnectionInput("c2", DatabaseProviderType.Postgres, true), "cs2")
                .GetAwaiter().GetResult().Id;
        }

        var rail = _ctx.RenderComponent<SchemaRail>();
        rail.Find("input[type=search]").Change("secr");
        Assert.DoesNotContain("orders", rail.Markup);

        rail.Find("select").Change(secondId.ToString());

        // A filter typed against one connection's tables means nothing against the next one's; leaving
        // it in place silently hid most of the new tree with no visible cause.
        Assert.Contains("orders", rail.Markup);
        Assert.Equal("", rail.Find("input[type=search]").GetAttribute("value"));
    }

    private static AngleSharp.Dom.IElement FindButton(IRenderedComponent<Connections> page, string text) =>
        page.FindAll("button").First(b => b.TextContent.Trim() == text);

    [Fact]
    public void Disposing_the_rail_unsubscribes_from_AppState_Changed()
    {
        var rail = _ctx.RenderComponent<SchemaRail>();
        var state = _ctx.Services.GetRequiredService<AppState>();

        Assert.Equal(1, SubscriberCount(state));

        // bUnit disposes every rendered root component (and, transitively, IDisposable descendants)
        // here, exactly as the real Blazor renderer does when a component leaves the render tree.
        _ctx.DisposeComponents();

        // If SchemaRail.Dispose() did not unsubscribe, this would still be 1 and the rail would keep
        // itself alive for the rest of the circuit every time it is mounted and torn down.
        Assert.Equal(0, SubscriberCount(state));
    }

    private static int SubscriberCount(AppState state) => SubscriberCount(state, "Changed");

    [Fact]
    public void Disposing_the_rail_unsubscribes_from_AppState_ConnectionsChanged()
    {
        _ctx.RenderComponent<SchemaRail>();
        var state = _ctx.Services.GetRequiredService<AppState>();

        Assert.Equal(1, SubscriberCount(state, "ConnectionsChanged"));

        _ctx.DisposeComponents();

        // Same leak as the Changed subscription, and a second event means a second chance to forget.
        Assert.Equal(0, SubscriberCount(state, "ConnectionsChanged"));
    }

    private static int SubscriberCount(AppState state, string eventName)
    {
        var field = typeof(AppState).GetField(eventName,
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var handler = (Delegate?)field!.GetValue(state);
        return handler?.GetInvocationList().Length ?? 0;
    }

    private async Task RemoveStoredSecretAsync(Guid id)
    {
        using var scope = _ctx.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SqlAgentDbContext>();
        var secrets = scope.ServiceProvider.GetRequiredService<ISecretStore>();
        var entity = await db.DatabaseConnections.FindAsync(id);
        await secrets.DeleteAsync(entity!.ConnectionStringSecretRef);
    }

    private async Task HideAsync(string table)
    {
        using var scope = _ctx.Services.CreateScope();
        var policies = scope.ServiceProvider.GetRequiredService<TablePolicyService>();
        await policies.SetVisibilityAsync(_connectionId, "public", table, false);
    }

    public void Dispose()
    {
        _ctx.Dispose();
        _conn.Dispose();
    }
}

file sealed class RailProviderStub : IDatabaseProvider
{
    public DatabaseProviderType ProviderType => DatabaseProviderType.Postgres;
    public Task<ConnectionTestResult> TestConnectionAsync(string cs, CancellationToken ct = default)
        => Task.FromResult(ConnectionTestResult.Ok(null, 0));

    public Task<DatabaseSchema> GetSchemaAsync(string cs, CancellationToken ct = default)
        => Task.FromResult(new DatabaseSchema([
            new SchemaTable("public", "orders",
                [new SchemaColumn("id", "int", false), new SchemaColumn("total", "numeric", true, Precision: 10, Scale: 2)],
                ["id"], [], []),
            new SchemaTable("public", "secrets",
                [new SchemaColumn("token", "text", false)], [], [], []),
        ]));

    public Task<QueryResultSet> ExecuteQueryAsync(string cs, string sql, QueryExecutionOptions o, CancellationToken ct = default)
        => Task.FromResult(new QueryResultSet([], [], false));
}
