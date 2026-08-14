using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SqlAgent.Core;
using SqlAgent.Host.Components.Pages;
using SqlAgent.Host.Web;
using SqlAgent.Storage;

namespace SqlAgent.Tests;

/// <summary>Provider double whose connection test is scriptable, so the page's two panels can be driven
/// through both outcomes without a live server. Takes the result as a delegate rather than exposing a
/// mutable property of its own file-local type: CS9051 forbids a file-local type in a field of
/// <see cref="DatabasePageTests"/> even when that field is private, so the script lives in the test class
/// instead, behind a `Func&lt;ConnectionTestResult&gt;` — a public, non-file-local type.</summary>
file sealed class PageFakeProvider(Func<ConnectionTestResult> result, DatabaseProviderType type) : IDatabaseProvider
{
    public DatabaseProviderType ProviderType => type;

    public Task<ConnectionTestResult> TestConnectionAsync(string cs, CancellationToken ct = default)
        => Task.FromResult(result());

    public Task<DatabaseSchema> GetSchemaAsync(string cs, CancellationToken ct = default)
        => Task.FromResult(new DatabaseSchema(
            [new SchemaTable("dbo", "orders", [new SchemaColumn("id", "int", false)], ["id"], [], [])]));

    public Task<QueryResultSet> ExecuteQueryAsync(
        string cs, string sql, QueryExecutionOptions options, CancellationToken ct = default)
        => throw new NotSupportedException();
}

public class DatabasePageTests : IDisposable
{
    private readonly SqliteConnection _conn = new("DataSource=:memory:");
    private readonly Bunit.TestContext _ctx = new();
    private ConnectionTestResult _result = ConnectionTestResult.Ok("PostgreSQL 16.0", 12);

    public DatabasePageTests()
    {
        _conn.Open();
        _ctx.Services.AddDbContext<SqlAgentDbContext>(o => o.UseSqlite(_conn));
        // Singleton, not scoped: ScopedRunner opens a fresh DI scope per call (SeedAsync's own scope
        // included), and a scoped in-memory store would lose whatever an earlier scope wrote the moment
        // that scope disposed. Every other test file in this suite registers it the same way.
        _ctx.Services.AddSingleton<ISecretStore, InMemorySecretStore>();
        _ctx.Services.AddScoped<DatabaseConnectionService>();
        _ctx.Services.AddScoped<TablePolicyService>();
        _ctx.Services.AddScoped<ConnectionTester>();
        _ctx.Services.AddSingleton<IDatabaseProvider>(
            new PageFakeProvider(() => _result, DatabaseProviderType.Postgres));
        _ctx.Services.AddSingleton<IDatabaseProviderRegistry, DatabaseProviderRegistry>();
        // ConnectionTester, ConnectionPanel, and ObjectsPanel all inject ILogger<T>. AddLogging registers
        // the open generic once; a NullLogger<ConnectionTester>.Instance registration would satisfy only
        // that one closed type and leave the components unresolvable.
        _ctx.Services.AddLogging();
        _ctx.Services.AddScoped<ScopedRunner>();
        _ctx.Services.AddScoped<AppState>();
        _ctx.Services.AddScoped<DialogService>();
        _ctx.Services.AddScoped<ShortcutService>();
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        using var scope = _ctx.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<SqlAgentDbContext>().Database.EnsureCreated();
    }

    public void Dispose() { _ctx.Dispose(); _conn.Dispose(); }

    private async Task<Guid> SeedAsync(string name = "warehouse")
    {
        using var scope = _ctx.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<DatabaseConnectionService>();
        return (await svc.CreateAsync(
            new DatabaseConnectionInput(name, DatabaseProviderType.Postgres, true), "cs")).Id;
    }

    private IRenderedComponent<DatabasePage> Render(Guid? id = null)
        => _ctx.RenderComponent<DatabasePage>(p => p.Add(x => x.Id, id));

    [Fact]
    public void A_new_database_starts_with_an_empty_form_and_no_objects_panel()
    {
        var page = Render();

        Assert.Equal("", page.Find("[data-testid=connection-name]").GetAttribute("value") ?? "");
        Assert.Empty(page.FindAll("[data-testid=objects-panel-slot]"));
        // Nothing to delete yet, so offering it would be a button that can only fail.
        Assert.Empty(page.FindAll("[data-testid=connection-delete]"));
    }

    [Fact]
    public async Task Opening_a_saved_database_tests_it_and_shows_the_objects_panel()
    {
        // Requiring a manual click on every visit would put a button between the user and the thing they
        // navigated to. A new connection still has to be tested by hand — there is nothing to test yet.
        var id = await SeedAsync();

        var page = Render(id);

        Assert.Contains("warehouse", page.Find("[data-testid=connection-name]").GetAttribute("value"));
        Assert.Single(page.FindAll("[data-testid=objects-panel-slot]"));
    }

    [Fact]
    public async Task The_connection_string_field_is_blank_when_editing()
    {
        // The secret is never sent to the browser, and blank on save means "keep what is stored". Both
        // rules are inherited verbatim from the page this one replaces.
        var id = await SeedAsync();

        var page = Render(id);

        Assert.Equal("", page.Find("[data-testid=connection-string]").GetAttribute("value") ?? "");
        Assert.Contains("keep", page.Find("[data-testid=connection-string]").GetAttribute("placeholder"),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_failed_test_hides_the_objects_panel_and_shows_a_classified_outcome()
    {
        var id = await SeedAsync();
        _result = ConnectionTestResult.Fail(
            ConnectionFailure.AuthenticationFailed, "28P01: password authentication failed", 4);

        var page = Render(id);

        Assert.Empty(page.FindAll("[data-testid=objects-panel-slot]"));
        Assert.Contains("connection_test_auth_failed", page.Markup);
        Assert.Contains("rejected the credentials", page.Markup);
    }

    [Fact]
    public async Task A_failed_test_never_renders_the_driver_text()
    {
        // The whole point of the classification. This is the assertion that would have caught the old
        // page rendering ex.Message straight through.
        var id = await SeedAsync();
        _result = ConnectionTestResult.Fail(
            ConnectionFailure.AuthenticationFailed,
            "28P01: password authentication failed for user \"sa\" (Host=secret-host;Password=hunter2)", 4);

        var page = Render(id);

        Assert.DoesNotContain("hunter2", page.Markup);
        Assert.DoesNotContain("secret-host", page.Markup);
        Assert.DoesNotContain("28P01", page.Markup);
    }

    [Fact]
    public async Task A_successful_test_reports_the_server_version_and_the_elapsed_time()
    {
        var id = await SeedAsync();

        var page = Render(id);

        Assert.Contains("PostgreSQL 16.0", page.Markup);
        Assert.Contains("12", page.Markup);
    }

    [Fact]
    public async Task A_test_records_the_dot_state_for_the_sidebar()
    {
        var id = await SeedAsync();
        var state = _ctx.Services.GetRequiredService<AppState>();

        Render(id);

        Assert.Equal(ConnectionStatus.Ok, state.StatusOf(id));
    }

    [Fact]
    public void Saving_a_new_database_without_a_connection_string_is_refused_with_a_code()
    {
        var page = Render();

        page.Find("[data-testid=connection-name]").Change("warehouse");
        page.Find("[data-testid=connection-save]").Click();

        Assert.Contains("connection_string_required", page.Markup);
    }

    [Fact]
    public async Task Saving_announces_the_change_so_the_sidebar_re_reads()
    {
        var page = Render();
        var state = _ctx.Services.GetRequiredService<AppState>();
        var raised = 0;
        state.ConnectionsChanged += () => raised++;

        page.Find("[data-testid=connection-name]").Change("warehouse");
        page.Find("[data-testid=connection-string]").Change("Host=localhost");
        await page.Find("[data-testid=connection-save]").ClickAsync(new MouseEventArgs());

        Assert.Equal(1, raised);
        using var scope = _ctx.Services.CreateScope();
        Assert.Single(await scope.ServiceProvider.GetRequiredService<DatabaseConnectionService>().ListAsync());
    }

    [Fact]
    public async Task Deleting_goes_through_a_confirmation()
    {
        // The one destructive operation on this page. Every other destructive operation in the app asks
        // first; the page this replaces deleted a connection on a single click with no question at all.
        var id = await SeedAsync();
        var page = Render(id);
        var dialogs = _ctx.Services.GetRequiredService<DialogService>();

        await page.Find("[data-testid=connection-delete]").ClickAsync(new MouseEventArgs());

        Assert.NotNull(dialogs.Current);
        using var scope = _ctx.Services.CreateScope();
        Assert.Single(await scope.ServiceProvider.GetRequiredService<DatabaseConnectionService>().ListAsync());
    }
}
