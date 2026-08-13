using System.Text.RegularExpressions;
using Bunit;
using Bunit.TestDoubles;
using Microsoft.AspNetCore.Components;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
    private readonly RecordingLoggerProvider _logs = new();

    public ShellTests()
    {
        // The sidebar hosts SchemaRail, which resolves the connection services, so the shell test needs
        // the same registrations the rail's own tests use.
        _conn.Open();
        RegisterSidebarServices(_ctx, _logs, "expanded");

        using var scope = _ctx.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<SqlAgentDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public void The_sidebar_renders_the_product_mark_and_the_routes_that_exist()
    {
        var sidebar = _ctx.RenderComponent<Sidebar>();

        Assert.Contains("SQL Agent", sidebar.Markup);
        // Phase B1 replaced the Workspace row: conversations are the front door now, and the SQL editor
        // keeps its own row rather than a tab inside a page. Search arrives in B2 with its modal.
        Assert.Contains("New chat", sidebar.Markup);
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
        RegisterSidebarServices(ctx, new RecordingLoggerProvider(), "collapsed");

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
    public void An_interop_timeout_during_setSidebar_is_caught_and_leaves_evidence()
    {
        // The third sibling the two-type filter missed. Blazor Server enforces
        // CircuitOptions.JSInteropDefaultCallTimeout (one minute by default) on every interop call, and
        // when it elapses the pending call is cancelled: awaiting it throws TaskCanceledException, which
        // derives from OperationCanceledException — a sibling of JSException and JSDisconnectedException
        // under System.Exception, caught by neither of those. A backgrounded tab throttled to a
        // standstill, or a browser that simply never answers, is enough to trigger it.
        //
        // What it does NOT do, contrary to the sibling tests above, is take the circuit down. An async
        // event handler that throws OperationCanceledException completes as a *cancelled* task (the
        // async method builder converts it via TrySetCanceled), and the framework's own
        // Renderer.GetErrorHandledTask guards its error path with "if (!taskToHandle.IsCanceled)" under
        // the comment "Ignore errors due to task cancellations" — so the exception is discarded before
        // any error boundary or circuit teardown sees it. Verified rather than assumed: with
        // OperationCanceledException removed from the filter, an escaping JSException fails these tests
        // and an escaping TaskCanceledException does not.
        //
        // That silence is the actual defect, and it is why this test asserts the log line rather than
        // "the page still works". Uncaught, the persistence failure is swallowed by the renderer with
        // no exception surfaced and no record written anywhere; the sidebar simply never remembers its
        // collapsed state and nothing in any log says why. Caught, it is Debug-level evidence — the
        // level is deliberate: an interop timeout is routine, unlike the missing-function case below.
        _ctx.JSInterop.SetupVoid("sqlAgentUi.setSidebar", _ => true)
            .SetException(new TaskCanceledException("JS interop call timed out"));
        var sidebar = _ctx.RenderComponent<Sidebar>();

        sidebar.Find("[data-testid=collapse-toggle]").Click();

        Assert.Contains("collapsed", sidebar.Find("aside").ClassName);
        var record = _logs.Records.Single(r => r.Message.Contains("sqlAgentUi.setSidebar failed"));
        Assert.Equal(LogLevel.Debug, record.Level);
        Assert.IsAssignableFrom<OperationCanceledException>(record.Exception);
    }

    [Fact]
    public void A_missing_interop_function_is_logged_loudly_but_a_gone_circuit_is_not()
    {
        // appsettings.json sets Logging:LogLevel:Default to Information with no Development override, so
        // every one of these catches logging at Debug meant the interop diagnostics two earlier fix
        // rounds bought emitted nothing at all in any configuration this project ships. The split is by
        // what the failure means: a JSException is sqlAgentUi.setSidebar being missing, renamed, or
        // throwing — a real bug that leaves the sidebar permanently unable to remember its state — so it
        // clears the Information default at Warning. A dropped circuit is routine and stays at Debug,
        // where it cannot drown the log every time a laptop sleeps.
        _ctx.JSInterop.SetupVoid("sqlAgentUi.setSidebar", _ => true)
            .SetException(new JSException("Could not find 'sqlAgentUi.setSidebar'"));
        var sidebar = _ctx.RenderComponent<Sidebar>();
        sidebar.Find("[data-testid=collapse-toggle]").Click();

        Assert.Equal(LogLevel.Warning,
            _logs.Records.Single(r => r.Message.Contains("sqlAgentUi.setSidebar failed")).Level);

        using var disconnected = new Bunit.TestContext();
        var otherLogs = new RecordingLoggerProvider();
        RegisterSidebarServices(disconnected, otherLogs, "expanded");
        disconnected.JSInterop.SetupVoid("sqlAgentUi.setSidebar", _ => true)
            .SetException(new JSDisconnectedException("circuit gone"));
        disconnected.RenderComponent<Sidebar>().Find("[data-testid=collapse-toggle]").Click();

        Assert.Equal(LogLevel.Debug,
            otherLogs.Records.Single(r => r.Message.Contains("sqlAgentUi.setSidebar failed")).Level);
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
    public void The_settings_row_is_rendered_and_points_at_the_settings_route()
    {
        // Retires No_settings_row_is_rendered (Task 5): that test pinned the absence of a Settings row
        // while /settings 404'd into Routes.razor's "Not found." Task 7 makes the route real, so the row
        // belongs back -- this asserts the row exists AND that it targets /settings specifically, not
        // just that the word "Settings" appears somewhere in the sidebar's markup.
        var sidebar = _ctx.RenderComponent<Sidebar>();

        var settingsLink = sidebar.Find("a[href='/settings']");
        Assert.Contains("Settings", settingsLink.TextContent);
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

    // --- Task 6 review findings ---------------------------------------------------------------

    [Fact]
    public void The_sidebar_itself_does_not_clip_so_the_users_menu_shadow_can_escape_it()
    {
        // UserCard (Task 6) puts a Menu in .sidebar-foot. Menu's .menu-panel is position:absolute with
        // left:0 and no right, so it shrink-to-fits within .sidebar's own 260px content box and was
        // never actually clipped horizontally or vertically by the old base overflow:hidden -- what that
        // clip cut was the panel's --shadow-menu drop shadow (box-shadow paints outside the border box
        // and follows the same clipping rule as any other visual effect on a clipped ancestor). bUnit
        // runs no CSS engine, so this can only be pinned on the stylesheet source, the same way the
        // other Sidebar.razor.css facts in this file are.
        var css = File.ReadAllText(RepoPaths.Find("src/SqlAgent.Host/Components/Layout/Sidebar.razor.css"));

        var baseRule = ExtractBlock(css, ".sidebar {");
        Assert.DoesNotContain("overflow", baseRule);
    }

    [Fact]
    public void The_collapsed_rails_own_clip_moves_to_where_it_is_still_needed()
    {
        // Removing .sidebar's blanket overflow:hidden (see the fact above) must not silently reopen the
        // bleed it was guarding against: at the 72px collapsed rail width, SidebarHeader's brand icon
        // and collapse-toggle button no longer fit side by side, and flexbox's default min-width:auto
        // refuses to shrink them below their own content size, so without a clip they would spill out
        // of the aside into the main content area. The replacement clip is scoped to
        // ".sidebar.collapsed" inside the wide-viewport media query specifically because .sidebar-foot
        // (the only descendant with a position:absolute popup) is display:none in that exact state, so
        // nothing that needs to escape the box is ever visible while this clip is active.
        var css = File.ReadAllText(RepoPaths.Find("src/SqlAgent.Host/Components/Layout/Sidebar.razor.css"));

        var wideBlock = ExtractBlock(css, "@media (min-width: 1024px)");
        Assert.Contains(".sidebar.collapsed { overflow: hidden; }", wideBlock);
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
        // Anchored under ".app aside.sidebar" (Task 6): the bare class names would otherwise hide every
        // .nav-label/.sidebar-body/.sidebar-foot on the page, not just the sidebar's own.
        Assert.Contains("html.sidebar-collapsed .app aside.sidebar .sidebar-body", css);
        Assert.Contains("html.sidebar-collapsed .app aside.sidebar .sidebar-foot", css);
    }

    [Fact]
    public void The_pre_paint_collapsed_rail_clips_itself_before_the_circuit_can()
    {
        // Sidebar.razor.css's ".sidebar.collapsed { overflow: hidden; }" only ever applies once
        // OnAfterRenderAsync's read-back has added the scoped "collapsed" class to <aside> -- which
        // cannot happen until the circuit connects, and never happens at all if it doesn't (a blocked
        // WebSocket, a JS error in getSidebar). From first paint until then, html.sidebar-collapsed is
        // the ONLY thing narrowing the rail to 72px and hiding sidebar-body/sidebar-foot (see the fact
        // above), so it has to carry its own clip too, or SidebarHeader's brand icon and collapse-toggle
        // button -- sized to their own content, flex's default min-width:auto refusing to shrink either
        // -- spill across the rail's border into the main content card for that entire window.
        var css = File.ReadAllText(RepoPaths.Find("src/SqlAgent.Host/wwwroot/css/app.css"));

        var preConnectRule = ExtractBlock(css, "html.sidebar-collapsed .app aside.sidebar {");
        Assert.Contains("overflow: hidden", preConnectRule);
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
    public void Escape_closes_an_open_drawer()
    {
        // The spec calls for the drawer to close on scrim click OR Escape; only the scrim was wired.
        // On a phone the scrim is a tap target, but on a narrow desktop window (the same sub-1024px
        // breakpoint) Escape is the reflex, and nothing answered it.
        var sidebar = _ctx.RenderComponent<Sidebar>();
        sidebar.Find("[data-testid=drawer-open]").Click();
        Assert.Contains("drawer-open", sidebar.Find("aside").ClassName);

        sidebar.Find("aside").KeyDown(Key.Escape);

        Assert.DoesNotContain("drawer-open", sidebar.Find("aside").ClassName);
    }

    [Fact]
    public void Opening_the_drawer_moves_focus_into_it_so_Escape_has_somewhere_to_bubble_from()
    {
        // The half of Escape-to-close that KeyDown() above cannot exercise. bUnit has no focus model at
        // all: KeyDown() invokes the handler directly and would pass even if focus never entered the
        // aside — but in a real browser the aside's @onkeydown only fires by bubbling from the focused
        // element, and the drawer is opened by a hamburger that sits OUTSIDE the aside, so without
        // moving focus inside, Escape is delivered to the trigger and the handler is dead code.
        //
        // The obvious fix, an autofocus attribute on the close button, does not work here and was
        // measured rather than reasoned about: a standalone Chromium probe that inserted an
        // autofocus-carrying button in a click handler left focus on the trigger and never fired the
        // container's keydown. The spec explains why — autofocus candidates are flushed only while the
        // document's focused area is still the body, and the opening click has already focused the
        // hamburger. So the move is explicit, via ElementReference.FocusAsync, which is interop
        // underneath; asserting the invocation is the only observable bUnit offers.
        var sidebar = _ctx.RenderComponent<Sidebar>();

        sidebar.Find("[data-testid=drawer-open]").Click();

        Assert.NotEqual(0, FocusInvocationCount());
    }

    [Fact]
    public void A_closed_drawer_wires_no_keydown_handler_so_typing_in_the_rail_costs_nothing()
    {
        // The Escape-to-close handler must not be attached while the drawer is closed, and that is a
        // performance fact rather than a tidiness one. SchemaRail's filter input lives inside this
        // <aside> and binds on @onchange rather than @oninput specifically so it does not send a round
        // trip per keystroke. A keydown handler on an ancestor undoes that through bubbling -- and worse
        // than the round trip alone, ComponentBase.HandleEventAsync calls StateHasChanged() after every
        // callback whether or not the callback did anything, so each keystroke would also re-render the
        // whole Sidebar subtree including the rail's table list. On a wide viewport _drawerOpen can never
        // be true, so all of that would be pure waste. An early return inside the handler cannot avoid
        // it: the round trip and the re-render happen either side of the callback regardless. The
        // attribute itself has to be absent.
        //
        // On the assertion. Two more obvious shapes were tried and rejected because they cannot fail:
        // bUnit does not throw for an unhandled keydown (it models bubbling, where "nobody handled it"
        // is legitimate), and RenderCount moves on any dispatched event whether or not a handler exists
        // -- a keydown on .sidebar-head, which has no handler at all, bumps it just the same. What does
        // discriminate is bUnit's own projection of the render tree into its DOM: an element with an
        // event-handler frame carries a "blazor:onkeydown" marker attribute. That is not real HTML and
        // never reaches a browser, but it is exactly the fact under test -- whether the render tree
        // registered a listener on this element -- and it is the only place bUnit exposes it.
        var sidebar = _ctx.RenderComponent<Sidebar>();

        Assert.False(sidebar.Find("aside").HasAttribute("blazor:onkeydown"),
            "A closed drawer must attach no keydown handler; every keystroke in SchemaRail's filter would otherwise cost a round trip and a full Sidebar re-render.");

        sidebar.Find("[data-testid=drawer-open]").Click();

        // Conditional, not simply absent -- otherwise the assertion above would pass forever while
        // Escape-to-close quietly stopped working. Escape_closes_an_open_drawer covers the behaviour.
        Assert.True(sidebar.Find("aside").HasAttribute("blazor:onkeydown"),
            "An open drawer must attach the keydown handler, or Escape cannot close it.");
    }

    [Fact]
    public void A_closed_drawer_is_out_of_the_tab_order_below_1024px()
    {
        // transform: translateX(-100%) moves the drawer off-screen but leaves every control in it
        // focusable, so Tab on a phone walks through an invisible sidebar before reaching the page —
        // pre-existing since Phase A, and worse now that history rows live in there too. visibility:
        // hidden is what actually removes a subtree from the tab order, and unlike an inert attribute it
        // can be scoped to the viewport where the drawer exists: above 1024px the sidebar is permanent
        // and must stay tabbable. bUnit runs no CSS engine, so this is pinned on source text.
        var css = File.ReadAllText(RepoPaths.Find("src/SqlAgent.Host/Components/Layout/Sidebar.razor.css"));

        var narrow = ExtractBlock(css, "@media (max-width: 1023px)");
        Assert.Contains("visibility: hidden", narrow);
        Assert.Contains(".sidebar.drawer-open", narrow);
        Assert.Contains("visibility: visible", narrow);
    }

    [Fact]
    public void Closing_the_drawer_returns_focus_to_the_hamburger_that_opened_it()
    {
        // Opening the drawer moves focus into it (Phase A). Closing it without giving focus back leaves
        // the focus ring on an element that is now hidden, so the next Tab restarts from the top of the
        // document — the classic dialog-dismissal defect, and the other half of carry-forward item 3.
        var sidebar = _ctx.RenderComponent<Sidebar>();
        sidebar.Find("[data-testid=drawer-open]").Click();
        var afterOpen = FocusInvocationCount();

        sidebar.Find(".sidebar-scrim").Click();

        // Exactly one more, not merely "more than before": a regression that never clears
        // _focusTriggerPending and re-focuses on every subsequent render would satisfy a bare ">"
        // assertion. The pending flag exists specifically to make this a once-only move.
        Assert.Equal(afterOpen + 1, FocusInvocationCount());
    }

    [Fact]
    public void The_drawer_focus_move_happens_only_when_the_drawer_actually_opens()
    {
        // Focus is a shared, user-visible resource: stealing it on every render would yank the caret out
        // of whatever the user was typing in the SQL editor each time the sidebar re-rendered, and
        // grabbing it on first render would fight the browser's own restore-focus-on-reload. The pending
        // flag exists to keep the move to exactly the open transition, and this pins that.
        //
        // Closing the drawer is no longer a counter-example here (Task 6): it now deliberately moves
        // focus back to the trigger, covered separately by
        // Closing_the_drawer_returns_focus_to_the_hamburger_that_opened_it. The re-render used below
        // (collapsing the sidebar) is neither an open nor a close, so it is still a valid case where no
        // focus move should happen.
        var sidebar = _ctx.RenderComponent<Sidebar>();

        Assert.Equal(0, FocusInvocationCount());

        sidebar.Find("[data-testid=drawer-open]").Click();
        var afterOpen = FocusInvocationCount();
        Assert.NotEqual(0, afterOpen);

        // A re-render that is neither an open nor a close (collapsing the rail) must not focus again.
        sidebar.Find("[data-testid=collapse-toggle]").Click();

        Assert.Equal(afterOpen, FocusInvocationCount());
    }

    [Fact]
    public void The_aside_is_focusable_enough_to_receive_a_bubbled_Escape()
    {
        // @onkeydown on a non-interactive element never fires without a tabindex: the element is not in
        // the focus path at all, so no keyboard event can reach it, bubbled or otherwise. -1 rather than
        // 0 keeps the aside out of the tab order, so Escape-to-close costs no phantom tab stop before
        // the brand link. Same shape as Menu.razor's .menu-root and Modal.razor's .modal-root.
        var sidebar = _ctx.RenderComponent<Sidebar>();

        Assert.Equal("-1", sidebar.Find("aside").GetAttribute("tabindex"));
    }

    [Fact]
    public void Disposing_the_sidebar_unsubscribes_from_navigation_changes()
    {
        var sidebar = _ctx.RenderComponent<Sidebar>();
        var nav = _ctx.Services.GetRequiredService<FakeNavigationManager>();

        // Not pinned to a literal count: SidebarNav renders one NavLink per route (three as of Task 7,
        // was two before), and each NavLink subscribes to LocationChanged internally (to compute its own
        // "active" class) independently of Sidebar's own subscription. Hardcoding that total is exactly
        // the trap this test fell into once already -- it broke the moment Task 7 added the Settings
        // row back, for a reason that had nothing to do with the leak this test exists to catch. What
        // this test actually cares about is the BEHAVIOR: at least one subscriber exists after render
        // (Sidebar's own, at minimum), and every one of them is gone after disposal.
        Assert.True(LocationChangedSubscriberCount(nav) > 0,
            "Expected Sidebar to hold a LocationChanged subscription after render.");

        // bUnit disposes the whole rendered component tree here, the same as the real Blazor renderer
        // does when a component leaves the render tree.
        _ctx.DisposeComponents();

        // If Sidebar.Dispose() did not unsubscribe, this would still be at least 1 and the component
        // would keep itself alive for the rest of the circuit -- the same class of leak
        // WorkAreaBoundaryTests pins for WorkArea's identical subscription. NavLink cleans up its own
        // subscriptions regardless, so a nonzero result here can only mean Sidebar's own leaked.
        Assert.Equal(0, LocationChangedSubscriberCount(nav));
    }

    /// <summary>How many times the component asked the browser to move focus. ElementReference.FocusAsync
    /// is interop underneath (Blazor's own "domWrapper.focus"), and the invocation record is the only
    /// trace of it bUnit exposes — there is no focus model to observe the result of.</summary>
    private int FocusInvocationCount() =>
        _ctx.JSInterop.Invocations.Count(i => i.Identifier.Contains("focus", StringComparison.OrdinalIgnoreCase));

    /// <summary>The registrations Sidebar needs to render at all: SchemaRail's connection services, plus
    /// HostInfo and a theme read-back for the UserCard/ThemeToggle in its foot. Shared by the fixture's
    /// own context and by the tests that need a second, differently-configured one.</summary>
    private void RegisterSidebarServices(Bunit.TestContext ctx, RecordingLoggerProvider logs, string sidebarState)
    {
        ctx.Services.AddDbContext<SqlAgentDbContext>(o => o.UseSqlite(_conn));
        ctx.Services.AddSingleton<ISecretStore, InMemorySecretStore>();
        ctx.Services.AddSingleton<IDatabaseProviderRegistry, DatabaseProviderRegistry>();
        ctx.Services.AddScoped<DatabaseConnectionService>();
        ctx.Services.AddScoped<TablePolicyService>();
        ctx.Services.AddScoped<ScopedRunner>();
        ctx.Services.AddScoped<AppState>();
        ctx.Services.AddLogging();
        // An explicit factory, not just an extra ILoggerProvider registration: bUnit pre-registers its
        // own ILoggerFactory, AddLogging()'s TryAdd leaves that in place, and it ignores providers
        // entirely — so records went nowhere. Registering the factory last makes it the one resolved.
        ctx.Services.AddSingleton<ILoggerFactory>(new LoggerFactory([logs]));
        ctx.Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        ctx.Services.AddSingleton<HostInfo>();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.JSInterop.Setup<string>("sqlAgentUi.getSidebar").SetResult(sidebarState);
        ctx.JSInterop.Setup<string>("sqlAgentUi.getTheme").SetResult("system");
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
        // Comments are stripped before searching, not after: A_closed_drawer_is_out_of_the_tab_order_
        // below_1024px pins a "visibility: hidden" declaration, and the explanatory comment right above
        // that declaration in Sidebar.razor.css contains that exact phrase in prose. Without stripping,
        // Assert.Contains("visibility: hidden", block) is satisfied by the comment alone and the
        // assertion would still pass with the declaration deleted -- pinning documentation, not the
        // rule. SidebarCollapseParityTests.StripComments exists for the identical reason.
        css = StripComments(css);
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

    private static string StripComments(string css) =>
        Regex.Replace(css, @"/\*.*?\*/", "", RegexOptions.Singleline);

    public void Dispose()
    {
        _ctx.Dispose();
        _conn.Dispose();
    }
}
