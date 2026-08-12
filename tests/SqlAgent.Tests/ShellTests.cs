using Bunit;
using Bunit.TestDoubles;
using Microsoft.AspNetCore.Components;
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

    // --- Task 5 review findings ---------------------------------------------------------------

    [Fact]
    public void No_settings_row_is_rendered()
    {
        // No /settings route exists yet -- the only @page directives in src/ are "/" and "/connections"
        // -- so a Settings nav row would 404 into Routes.razor's "Not found." inside the shell. The
        // brief's own scope note forbids exactly this: a nav row that does nothing is worse than the
        // link it replaced. Task 7 adds the row back once the page is real.
        var sidebar = _ctx.RenderComponent<Sidebar>();

        Assert.DoesNotContain("Settings", sidebar.Markup);
    }

    [Fact]
    public void The_nav_row_stylesheet_reaches_the_anchors_NavLink_actually_renders()
    {
        // NavLink is a Razor component, not an HTML element, and Blazor's scoped CSS only stamps the
        // scope attribute onto elements a file's own markup renders -- so a bare ".nav-row { ... }"
        // selector in SidebarNav.razor.css compiles cleanly but matches nothing at runtime: the
        // generated bundle emits ".nav-row[b-xxxxx]" while the <a> NavLink renders carries only
        // class="nav-row", with no scope attribute. bUnit renders markup but runs no browser and no CSS
        // engine at all, so this can only be pinned by reading the stylesheet source and asserting the
        // fix -- routing through the parent <nav>'s own scope via ::deep, the same technique
        // SidebarHeader.razor.css already uses for .brand ::deep .brand-mark -- is actually present.
        var css = File.ReadAllText(RepoPaths.Find("src/SqlAgent.Host/Components/Layout/SidebarNav.razor.css"));

        Assert.Contains(".sidebar-nav ::deep .nav-row {", css);
        Assert.Contains(".sidebar-nav ::deep .nav-row:hover", css);
        Assert.Contains(".sidebar-nav ::deep .nav-row.active", css);
    }

    [Fact]
    public void The_collapsed_hiding_rules_are_guarded_to_wide_viewports()
    {
        // _collapsed is viewport-independent state carried in from localStorage, but below 1024px the
        // sidebar becomes a fixed-width drawer, not a narrow rail -- and the collapse toggle that would
        // undo a collapsed state is itself display:none below 1024px. Without a min-width guard, a user
        // who collapsed on desktop and later opens the drawer on a phone gets a drawer with no
        // SchemaRail (the only connection/table picker until a later phase), no user card, and no way
        // back in short of widening the window or clearing localStorage. bUnit runs no CSS engine, so
        // this is pinned on the source text, the same way DesignSystemTests pins app.css structure.
        var css = File.ReadAllText(RepoPaths.Find("src/SqlAgent.Host/Components/Layout/Sidebar.razor.css"));

        var wideBlock = ExtractBlock(css, "@media (min-width: 1024px)");
        Assert.Contains(".sidebar.collapsed ::deep .nav-label", wideBlock);
        Assert.Contains(".sidebar.collapsed ::deep .brand-name", wideBlock);
        Assert.Contains(".sidebar.collapsed .sidebar-body", wideBlock);
        Assert.Contains(".sidebar.collapsed .sidebar-foot", wideBlock);

        // Regression guard: these must not also sit in the narrow (drawer) block, or unguarded above
        // every media query, where they would apply at every viewport including the drawer.
        var narrowBlock = ExtractBlock(css, "@media (max-width: 1023px)");
        Assert.DoesNotContain(".sidebar-body", narrowBlock);
        Assert.DoesNotContain(".sidebar-foot", narrowBlock);
        Assert.DoesNotContain(".nav-label", narrowBlock);
    }

    [Fact]
    public void The_app_stylesheet_reads_back_the_pre_paint_collapsed_class()
    {
        // theme.js sets html.sidebar-collapsed on <html> before first paint specifically so a collapsed
        // sidebar does not render wide and then snap narrow -- but no stylesheet ever read that class,
        // so every collapsed user watched exactly that snap on every page load once the circuit
        // connected and Sidebar's own OnAfterRenderAsync read-back applied the "collapsed" class a
        // second time, this time to a scoped element that CSS transitions animate. The rule has to live
        // in app.css, not Sidebar.razor.css: the latter is scoped ([b-xxxxx]) and can never match a
        // plain "html.sidebar-collapsed" ancestor selector.
        var css = File.ReadAllText(RepoPaths.Find("src/SqlAgent.Host/wwwroot/css/app.css"));

        Assert.Contains("html.sidebar-collapsed .app aside.sidebar", css);
        Assert.Contains("html.sidebar-collapsed .sidebar-body", css);
        Assert.Contains("html.sidebar-collapsed .sidebar-foot", css);
    }

    [Fact]
    public void Navigating_closes_an_open_drawer()
    {
        // MainLayout, which hosts Sidebar, is not recreated across route navigation, so nothing else
        // clears _drawerOpen once a nav row is tapped on a phone -- the route would change behind a
        // still-open drawer and scrim, obscuring the very page the tap asked for. Mirrors WorkArea's own
        // LocationChanged subscription for the analogous problem (a tripped error boundary surviving
        // navigation).
        var sidebar = _ctx.RenderComponent<Sidebar>();
        sidebar.Find("[data-testid=drawer-open]").Click();
        Assert.Contains("drawer-open", sidebar.Find("aside").ClassName);

        var nav = _ctx.Services.GetRequiredService<FakeNavigationManager>();
        nav.NavigateTo("connections");

        Assert.DoesNotContain("drawer-open", sidebar.Find("aside").ClassName);
    }

    [Fact]
    public void Disposing_the_sidebar_unsubscribes_from_navigation_changes()
    {
        var sidebar = _ctx.RenderComponent<Sidebar>();
        var nav = _ctx.Services.GetRequiredService<FakeNavigationManager>();

        // 3, not 1: SidebarNav renders two NavLink components, and NavLink subscribes to
        // LocationChanged internally (to compute its own "active" class) independently of Sidebar's own
        // subscription. The count only needs to be stable and non-zero here; what this test actually
        // pins is that it drops to exactly 0 after disposal below.
        Assert.Equal(3, LocationChangedSubscriberCount(nav));

        // bUnit disposes the whole rendered component tree here, the same as the real Blazor renderer
        // does when a component leaves the render tree.
        _ctx.DisposeComponents();

        // If Sidebar.Dispose() did not unsubscribe, this would still be at least 1 and the component
        // would keep itself alive for the rest of the circuit -- the same class of leak
        // WorkAreaBoundaryTests pins for WorkArea's identical subscription. NavLink cleans up its own
        // two subscriptions regardless, so a nonzero result here can only mean Sidebar's own leaked.
        Assert.Equal(0, LocationChangedSubscriberCount(nav));
    }

    private static int LocationChangedSubscriberCount(NavigationManager nav)
    {
        var field = typeof(NavigationManager).GetField("_locationChanged",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var handler = (Delegate?)field!.GetValue(nav);
        return handler?.GetInvocationList().Length ?? 0;
    }

    /// <summary>Extracts the brace-balanced body of the first block starting at <paramref name="selector"/>,
    /// unlike DesignSystemTests.Block, which assumes no nested braces -- a @media block's body is itself
    /// made of nested rule blocks, so the first "}" encountered would close only the first nested rule.</summary>
    private static string ExtractBlock(string css, string selector)
    {
        var start = css.IndexOf(selector, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Expected to find '{selector}' in the stylesheet.");
        var open = css.IndexOf('{', start);
        var depth = 0;
        var i = open;
        for (; i < css.Length; i++)
        {
            if (css[i] == '{') depth++;
            else if (css[i] == '}' && --depth == 0) break;
        }
        return css[(open + 1)..i];
    }

    public void Dispose()
    {
        _ctx.Dispose();
        _conn.Dispose();
    }
}
