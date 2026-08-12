using Bunit;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using SqlAgent.Core;
using SqlAgent.Host.Components.Layout;
using SqlAgent.Host.Web;
using SqlAgent.Storage;

namespace SqlAgent.Tests;

public class ShellTests : IDisposable
{
    private readonly SqliteConnection _conn = new("DataSource=:memory:");
    private readonly Bunit.TestContext _ctx = new();

    public ShellTests()
    {
        // The sidebar hosts SchemaRail, which resolves the connection services, so the shell test needs
        // the same registrations the rail's own tests use.
        _conn.Open();
        _ctx.Services.AddDbContext<SqlAgentDbContext>(o => o.UseSqlite(_conn));
        _ctx.Services.AddSingleton<ISecretStore, InMemorySecretStore>();
        _ctx.Services.AddSingleton<IDatabaseProviderRegistry, DatabaseProviderRegistry>();
        _ctx.Services.AddScoped<DatabaseConnectionService>();
        _ctx.Services.AddScoped<TablePolicyService>();
        _ctx.Services.AddScoped<ScopedRunner>();
        _ctx.Services.AddScoped<AppState>();
        _ctx.Services.AddLogging();
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        _ctx.JSInterop.Setup<string>("sqlAgentUi.getSidebar").SetResult("expanded");

        using var scope = _ctx.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<SqlAgentDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public void The_sidebar_renders_the_product_mark_and_the_routes_that_exist()
    {
        var sidebar = _ctx.RenderComponent<Sidebar>();

        Assert.Contains("SQL Agent", sidebar.Markup);
        // Phase A ships the routes that exist. New Chat and Search arrive in Phase B with the pages
        // behind them; a button that does nothing is worse than the link it replaced.
        Assert.Contains("Workspace", sidebar.Markup);
        Assert.Contains("Connections", sidebar.Markup);
    }

    [Fact]
    public void Collapsing_the_sidebar_marks_it_collapsed_and_persists_the_choice()
    {
        var sidebar = _ctx.RenderComponent<Sidebar>();

        sidebar.Find("[data-testid=collapse-toggle]").Click();

        Assert.Contains("collapsed", sidebar.Find("aside").ClassName);
        var invocation = _ctx.JSInterop.VerifyInvoke("sqlAgentUi.setSidebar");
        Assert.Equal("collapsed", invocation.Arguments[0]);
    }

    [Fact]
    public void The_collapsed_state_is_read_back_from_the_browser_on_first_render()
    {
        // Same reason as the theme: the class is applied to <html> pre-paint, so the component has to
        // ask rather than assume, or an expanded-looking sidebar renders inside a narrow shell.
        using var ctx = new Bunit.TestContext();
        ctx.Services.AddDbContext<SqlAgentDbContext>(o => o.UseSqlite(_conn));
        ctx.Services.AddSingleton<ISecretStore, InMemorySecretStore>();
        ctx.Services.AddSingleton<IDatabaseProviderRegistry, DatabaseProviderRegistry>();
        ctx.Services.AddScoped<DatabaseConnectionService>();
        ctx.Services.AddScoped<TablePolicyService>();
        ctx.Services.AddScoped<ScopedRunner>();
        ctx.Services.AddScoped<AppState>();
        ctx.Services.AddLogging();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.JSInterop.Setup<string>("sqlAgentUi.getSidebar").SetResult("collapsed");

        var sidebar = ctx.RenderComponent<Sidebar>();

        Assert.Contains("collapsed", sidebar.Find("aside").ClassName);
    }

    [Fact]
    public void The_drawer_opens_and_closes_for_narrow_viewports()
    {
        var sidebar = _ctx.RenderComponent<Sidebar>();

        sidebar.Find("[data-testid=drawer-open]").Click();
        Assert.Contains("drawer-open", sidebar.Find("aside").ClassName);

        sidebar.Find(".sidebar-scrim").Click();
        Assert.DoesNotContain("drawer-open", sidebar.Find("aside").ClassName);
    }

    [Fact]
    public void A_browser_that_cannot_persist_the_collapse_choice_does_not_take_the_circuit_down()
    {
        // theme.js failed to load, or drifted from this component, so sqlAgentUi.setSidebar is missing
        // or throws by the time the user clicks. JSException and JSDisconnectedException are siblings
        // under System.Exception, not a base/derived pair (see ThemeToggle.ApplyAsync for the full
        // reasoning), so Sidebar.ToggleCollapseAsync's catch must name both explicitly. An unhandled
        // exception escaping this click handler would take the whole circuit down: Sidebar is mounted
        // outside WorkArea's ErrorBoundary, so nothing else would catch it.
        _ctx.JSInterop.SetupVoid("sqlAgentUi.setSidebar", _ => true)
            .SetException(new JSException("Could not find 'sqlAgentUi.setSidebar'"));
        var sidebar = _ctx.RenderComponent<Sidebar>();

        sidebar.Find("[data-testid=collapse-toggle]").Click();

        // The visible width still flips even though persistence failed — reverting it here would just
        // fight the user's own click, since the toggle's visual effect already happened synchronously.
        Assert.Contains("collapsed", sidebar.Find("aside").ClassName);
    }

    [Fact]
    public void A_dropped_circuit_during_setSidebar_does_not_take_itself_down()
    {
        // The WebSocket drops mid-click (laptop sleep, network blip, backgrounded tab past the
        // transport timeout): InvokeVoidAsync throws JSDisconnectedException, not JSException. A catch
        // clause naming only JSException would let this one escape and kill the circuit outright, which
        // is strictly worse than a plain disconnect — it forfeits Blazor's own reconnect window and
        // hard-reloads the page, losing whatever the user had unsaved in the SQL editor.
        _ctx.JSInterop.SetupVoid("sqlAgentUi.setSidebar", _ => true)
            .SetException(new JSDisconnectedException("circuit gone"));
        var sidebar = _ctx.RenderComponent<Sidebar>();

        sidebar.Find("[data-testid=collapse-toggle]").Click();

        Assert.Contains("collapsed", sidebar.Find("aside").ClassName);
    }

    [Fact]
    public void The_schema_rail_still_lives_in_the_sidebar()
    {
        // Phase A must not remove a working surface. The rail is the only visibility control until the
        // config page lands in Phase C.
        var sidebar = _ctx.RenderComponent<Sidebar>();

        Assert.Contains("Connection", sidebar.Markup);
    }

    public void Dispose()
    {
        _ctx.Dispose();
        _conn.Dispose();
    }
}
