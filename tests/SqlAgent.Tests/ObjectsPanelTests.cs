using Bunit;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SqlAgent.Core;
using SqlAgent.Core.Policy;
using SqlAgent.Host.Components.Shared.Database;
using SqlAgent.Host.Web;
using SqlAgent.Storage;

namespace SqlAgent.Tests;

file sealed class ObjectsFakeProvider(DatabaseProviderType type) : IDatabaseProvider
{
    public DatabaseProviderType ProviderType => type;

    public Task<ConnectionTestResult> TestConnectionAsync(string cs, CancellationToken ct = default)
        => Task.FromResult(ConnectionTestResult.Ok(null, 0));

    public Task<DatabaseSchema> GetSchemaAsync(string cs, CancellationToken ct = default)
        => Task.FromResult(new DatabaseSchema(
            [new SchemaTable("dbo", "orders", [new SchemaColumn("id", "int", false)], ["id"], [], []),
             new SchemaTable("dbo", "customers", [new SchemaColumn("id", "int", false)], [], [], []),
             new SchemaTable("sales", "leads", [new SchemaColumn("id", "int", false)], [], [], [])],
            [new SchemaView("dbo", "order_summary", [new SchemaColumn("id", "int", false)])]));

    public Task<QueryResultSet> ExecuteQueryAsync(
        string cs, string sql, QueryExecutionOptions options, CancellationToken ct = default)
        => throw new NotSupportedException();
}

public class ObjectsPanelTests : IDisposable
{
    private readonly SqliteConnection _conn = new("DataSource=:memory:");
    private readonly Bunit.BunitContext _ctx = new();

    public ObjectsPanelTests()
    {
        _conn.Open();
        _ctx.Services.AddDbContext<SqlAgentDbContext>(o => o.UseSqlite(_conn));
        // InMemorySecretStore holds a private, non-static dictionary, so it must be a singleton: the
        // panel's ScopedRunner opens a fresh DI scope per action, and a scoped registration would make the
        // secret set here invisible to it. Every other test file in the suite registers it the same way,
        // matching Program.cs's own dev-fallback registration.
        _ctx.Services.AddSingleton<ISecretStore, InMemorySecretStore>();
        _ctx.Services.AddScoped<DatabaseConnectionService>();
        _ctx.Services.AddScoped<TablePolicyService>();
        _ctx.Services.AddSingleton<IDatabaseProvider>(new ObjectsFakeProvider(DatabaseProviderType.Postgres));
        _ctx.Services.AddSingleton<IDatabaseProviderRegistry, DatabaseProviderRegistry>();
        _ctx.Services.AddLogging();
        _ctx.Services.AddScoped<ScopedRunner>();
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        using var scope = _ctx.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<SqlAgentDbContext>().Database.EnsureCreated();
    }

    public void Dispose() { _ctx.Dispose(); _conn.Dispose(); }

    private async Task<Guid> SeedAsync()
    {
        using var scope = _ctx.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<DatabaseConnectionService>();
        return (await svc.CreateAsync(
            new DatabaseConnectionInput("c", DatabaseProviderType.Postgres, false), "cs")).Id;
    }

    private async Task<IRenderedComponent<ObjectsPanel>> RenderAsync()
    {
        var id = await SeedAsync();
        return _ctx.Render<ObjectsPanel>(p => p.Add(x => x.ConnectionId, id));
    }

    private async Task<ObjectAccess> LevelAsync(string name)
    {
        using var scope = _ctx.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SqlAgentDbContext>();
        var row = await db.TablePolicies.SingleOrDefaultAsync(p => p.TableName == name);
        return row is null ? ObjectAccess.Full
            : !row.IsVisible ? ObjectAccess.Hidden
            : row.CanWrite ? ObjectAccess.Full
            : ObjectAccess.ReadOnly;
    }

    [Fact]
    public async Task Objects_are_grouped_under_their_schema()
    {
        var panel = await RenderAsync();

        var headers = panel.FindAll(".schema-header").Select(h => h.TextContent).ToList();
        Assert.Contains(headers, h => h.Contains("dbo"));
        Assert.Contains(headers, h => h.Contains("sales"));
    }

    [Fact]
    public async Task Groups_start_collapsed_when_there_is_more_than_one_schema()
    {
        // The answer to a database with hundreds of objects, in place of a virtualized list: nothing
        // renders until the user says which schema they came for.
        var panel = await RenderAsync();

        Assert.Empty(panel.FindAll(".object-row"));

        await panel.FindAll("[data-testid^=schema-toggle-]").First().ClickAsync(new MouseEventArgs());
        Assert.NotEmpty(panel.FindAll(".object-row"));
    }

    [Fact]
    public async Task A_view_is_labelled_and_offers_two_levels_rather_than_three()
    {
        var panel = await RenderAsync();
        await panel.Find("[data-testid=schema-toggle-dbo]").ClickAsync(new MouseEventArgs());

        var row = panel.Find("[data-testid=object-row-dbo-order_summary]");
        Assert.Contains("View", row.TextContent);
        Assert.Single(row.QuerySelectorAll(".object-identity .badge"));
        Assert.Equal(2, row.QuerySelectorAll(".segment").Length);
    }

    [Fact]
    public async Task A_table_offers_all_three_levels()
    {
        var panel = await RenderAsync();
        await panel.Find("[data-testid=schema-toggle-dbo]").ClickAsync(new MouseEventArgs());

        Assert.Equal(3, panel.Find("[data-testid=object-row-dbo-orders]").QuerySelectorAll(".segment").Length);
    }

    [Fact]
    public async Task Choosing_a_level_writes_it_immediately()
    {
        // No Save button: the rail this replaces wrote on every toggle, and a panel that batches changes
        // needs a story for what happens when the user navigates away mid-edit.
        var panel = await RenderAsync();
        await panel.Find("[data-testid=schema-toggle-dbo]").ClickAsync(new MouseEventArgs());

        var readOnly = panel.Find("[data-testid=object-row-dbo-orders] .segment:nth-child(2)");
        await readOnly.ClickAsync(new MouseEventArgs());

        Assert.Equal(ObjectAccess.ReadOnly, await LevelAsync("orders"));
    }

    [Fact]
    public async Task A_schema_header_sets_every_object_beneath_it()
    {
        var panel = await RenderAsync();

        await panel.Find("[data-testid=schema-set-dbo] .segment:first-child").ClickAsync(new MouseEventArgs());

        Assert.Equal(ObjectAccess.Hidden, await LevelAsync("orders"));
        Assert.Equal(ObjectAccess.Hidden, await LevelAsync("customers"));
        Assert.Equal(ObjectAccess.Hidden, await LevelAsync("order_summary"));
        Assert.Equal(ObjectAccess.Full, await LevelAsync("leads"));
    }

    [Fact]
    public async Task The_filter_narrows_the_list_by_name()
    {
        var panel = await RenderAsync();
        await panel.Find("[data-testid=schema-toggle-dbo]").ClickAsync(new MouseEventArgs());

        panel.Find("[data-testid=objects-filter]").Input("cust");

        Assert.Single(panel.FindAll(".object-row"));
        Assert.Contains("customers", panel.Find(".object-row").TextContent);
    }

    [Fact]
    public async Task Filtering_opens_the_groups_that_still_have_matches()
    {
        // A filter that leaves every group collapsed shows the user nothing and reads as "no results".
        var panel = await RenderAsync();

        panel.Find("[data-testid=objects-filter]").Input("leads");

        Assert.Single(panel.FindAll(".object-row"));
        Assert.Contains("leads", panel.Find(".object-row").TextContent);
    }

    [Fact]
    public async Task A_filter_matching_nothing_says_so()
    {
        var panel = await RenderAsync();

        panel.Find("[data-testid=objects-filter]").Input("zzz");

        Assert.Empty(panel.FindAll(".object-row"));
        Assert.Contains("No objects match", panel.Markup);
    }

    [Fact]
    public async Task A_level_survives_a_reload_of_the_panel()
    {
        var panel = await RenderAsync();
        await panel.Find("[data-testid=schema-toggle-dbo]").ClickAsync(new MouseEventArgs());
        await panel.Find("[data-testid=object-row-dbo-orders] .segment:first-child")
            .ClickAsync(new MouseEventArgs());

        // Ruling H: ReloadAsync does not clear the open-groups set, so the dbo group is still open after
        // the reload. Clicking the toggle again here would close it and the assertion below would find
        // nothing — so we assert directly without touching the toggle a second time.
        await panel.InvokeAsync(async () => await panel.Instance.ReloadAsync());

        Assert.Contains("selected",
            panel.Find("[data-testid=object-row-dbo-orders] .segment:first-child").GetAttribute("class"));
    }
}
