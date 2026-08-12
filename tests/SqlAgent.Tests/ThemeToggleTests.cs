using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using SqlAgent.Host.Components.Shared.Ui;

namespace SqlAgent.Tests;

public class ThemeToggleTests
{
    // ThemeToggle injects ILogger<ThemeToggle> (both interop catches log through it), so every test
    // needs logging registered or bUnit's DI container fails to resolve the component at all. Tests that
    // care about what was logged pass a recorder; the rest keep the default no-op factory.
    private static Bunit.TestContext NewContext(RecordingLoggerProvider? logs = null)
    {
        var ctx = new Bunit.TestContext();
        try
        {
            ctx.Services.AddLogging();
            // An explicit factory, not just an extra ILoggerProvider registration: bUnit pre-registers
            // its own ILoggerFactory, AddLogging()'s TryAdd leaves that in place, and it ignores
            // providers entirely — so records went nowhere. Registering it last makes it the one
            // resolved.
            if (logs is not null) ctx.Services.AddSingleton<ILoggerFactory>(new LoggerFactory([logs]));
            return ctx;
        }
        catch
        {
            // AddLogging() throwing would otherwise leak ctx: it isn't behind a `using` until this
            // method returns it to the caller.
            ctx.Dispose();
            throw;
        }
    }

    [Fact]
    public void The_stored_theme_is_read_from_the_browser_on_first_render()
    {
        // The server cannot know the theme: it lives in localStorage, applied by theme.js before the
        // circuit connects. If the toggle rendered its own default instead of reading it back, the
        // control would show "System" on a page that is actually pinned to dark.
        using var ctx = NewContext();
        ctx.JSInterop.Setup<string>("sqlAgentUi.getTheme").SetResult("dark");

        var toggle = ctx.RenderComponent<ThemeToggle>();

        var dark = toggle.FindAll("button").Single(b => b.TextContent.Contains("Dark"));
        Assert.Equal("true", dark.GetAttribute("aria-pressed"));
    }

    [Fact]
    public void Choosing_a_theme_pushes_it_to_the_browser()
    {
        using var ctx = NewContext();
        ctx.JSInterop.Setup<string>("sqlAgentUi.getTheme").SetResult("system");
        // bUnit's JSInterop defaults to strict mode (see WorkspaceTests), so the setTheme call the click
        // below triggers must be planned or it throws Bunit.JSRuntimeUnhandledInvocationException —
        // which ApplyAsync's catch does not swallow. That filter names JSException,
        // JSDisconnectedException and OperationCanceledException; bUnit's type is none of those and does
        // not derive from any of them, so an unplanned call still fails this test loudly rather than
        // being absorbed into the same silence a real interop failure gets.
        ctx.JSInterop.SetupVoid("sqlAgentUi.setTheme", _ => true);
        var toggle = ctx.RenderComponent<ThemeToggle>();

        toggle.FindAll("button").Single(b => b.TextContent.Contains("Light")).Click();

        var invocation = ctx.JSInterop.VerifyInvoke("sqlAgentUi.setTheme");
        Assert.Equal("light", invocation.Arguments[0]);
    }

    [Fact]
    public void All_three_theme_choices_are_offered()
    {
        using var ctx = NewContext();
        ctx.JSInterop.Setup<string>("sqlAgentUi.getTheme").SetResult("system");

        var toggle = ctx.RenderComponent<ThemeToggle>();

        Assert.Equal(3, toggle.FindAll("button").Count);
        Assert.Contains("System", toggle.Markup);
        Assert.Contains("Light", toggle.Markup);
        Assert.Contains("Dark", toggle.Markup);
    }

    [Fact]
    public void A_browser_that_cannot_report_a_theme_falls_back_to_system()
    {
        // JSDisconnectedException on a torn-down circuit, or a private mode where localStorage throws,
        // must not take the page down through WorkArea's boundary.
        using var ctx = NewContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Strict;
        ctx.JSInterop.Setup<string>("sqlAgentUi.getTheme").SetException(new InvalidOperationException("no storage"));

        var toggle = ctx.RenderComponent<ThemeToggle>();

        var system = toggle.FindAll("button").Single(b => b.TextContent.Contains("System"));
        Assert.Equal("true", system.GetAttribute("aria-pressed"));
    }

    [Fact]
    public void A_junk_stored_value_still_highlights_System()
    {
        // getTheme() returns the raw localStorage string with no validation upstream (theme.js). A
        // hand-edited devtools entry, or a value written by some future version, must not become the
        // Value passed to Segmented: Segmented selects by exact match, so an unrecognized string would
        // highlight nothing and leave the control looking broken with no indication why. "system" is
        // the same fallback theme.js itself uses for a missing/invalid value.
        using var ctx = NewContext();
        ctx.JSInterop.Setup<string>("sqlAgentUi.getTheme").SetResult("purple-haze");

        var toggle = ctx.RenderComponent<ThemeToggle>();

        var system = toggle.FindAll("button").Single(b => b.TextContent.Contains("System"));
        Assert.Equal("true", system.GetAttribute("aria-pressed"));
        Assert.Single(toggle.FindAll("button[aria-pressed='true']"));
    }

    [Fact]
    public void A_browser_that_cannot_apply_a_theme_does_not_take_the_circuit_down()
    {
        // theme.js loaded fine at first render (getTheme succeeded, painting a normal-looking toggle),
        // but sqlAgentUi.setTheme is missing or throws by the time the user clicks — a stale cache, an
        // extension blocking the script's continued execution, or a future drift between this component
        // and theme.js. An unhandled JSException out of an event-handler callback is fatal to a Blazor
        // Server circuit, and ThemeToggle is mounted in the sidebar (Task 5), outside WorkArea's
        // ErrorBoundary, so nothing else would catch it. The clicked option must still show as selected:
        // applyTheme() never ran either way, so reverting the selection would just fight the user's click.
        using var ctx = NewContext();
        ctx.JSInterop.Setup<string>("sqlAgentUi.getTheme").SetResult("system");
        ctx.JSInterop.SetupVoid("sqlAgentUi.setTheme", _ => true)
            .SetException(new JSException("Could not find 'sqlAgentUi.setTheme'"));
        var toggle = ctx.RenderComponent<ThemeToggle>();

        toggle.FindAll("button").Single(b => b.TextContent.Contains("Dark")).Click();

        var dark = toggle.FindAll("button").Single(b => b.TextContent.Contains("Dark"));
        Assert.Equal("true", dark.GetAttribute("aria-pressed"));
    }

    [Fact]
    public void A_dropped_circuit_during_setTheme_does_not_take_itself_down()
    {
        // JSDisconnectedException and JSException are siblings under System.Exception, not a
        // base/derived pair -- a catch clause naming only one of them does not also catch the other.
        // This covers the case the previous fix-up missed: the WebSocket drops mid-click (laptop sleep,
        // network blip, backgrounded tab past the transport timeout) and InvokeVoidAsync throws
        // JSDisconnectedException, not JSException. An unhandled exception here would escape the event
        // handler and terminate the circuit outright rather than leaving it in Blazor's reconnect
        // window, which is strictly worse than a plain disconnect: the client's automatic reconnect
        // never gets the chance, and the page hard-reloads, losing whatever the user had unsaved in the
        // editor. The clicked option must still show as selected, same reasoning as the JSException test
        // above: applyTheme() never ran either way.
        using var ctx = NewContext();
        ctx.JSInterop.Setup<string>("sqlAgentUi.getTheme").SetResult("system");
        ctx.JSInterop.SetupVoid("sqlAgentUi.setTheme", _ => true)
            .SetException(new JSDisconnectedException("circuit gone"));
        var toggle = ctx.RenderComponent<ThemeToggle>();

        toggle.FindAll("button").Single(b => b.TextContent.Contains("Dark")).Click();

        var dark = toggle.FindAll("button").Single(b => b.TextContent.Contains("Dark"));
        Assert.Equal("true", dark.GetAttribute("aria-pressed"));
    }

    [Fact]
    public void An_interop_timeout_during_setTheme_is_caught_and_leaves_evidence()
    {
        // The third sibling, and the one two earlier fix rounds missed. Blazor Server enforces
        // CircuitOptions.JSInteropDefaultCallTimeout (one minute by default) on every interop call; when
        // it elapses — a backgrounded tab throttled to a standstill, a wedged renderer, a browser that
        // simply never answers — the pending call is cancelled and InvokeVoidAsync throws
        // OperationCanceledException (TaskCanceledException in practice, which derives from it). That is
        // a sibling of JSException and JSDisconnectedException under System.Exception, not a base or
        // derived type of either, so the previous two-type filter caught neither and it escaped this
        // handler.
        //
        // Unlike the two tests above, what it escaped INTO does not kill the circuit: an async event
        // handler throwing OperationCanceledException completes as a cancelled task, and the framework's
        // Renderer.GetErrorHandledTask skips its error path when taskToHandle.IsCanceled, commented in
        // the ASP.NET Core source as "Ignore errors due to task cancellations". Verified rather than
        // assumed — with this type removed from the filter, an escaping JSException fails these tests
        // and an escaping TaskCanceledException does not.
        //
        // So the defect is silence, not a crash, and that is what this test pins. Uncaught, the theme
        // simply never persists and the renderer discards the reason: nothing throws, nothing logs,
        // nothing anywhere records that the write failed. Caught, there is a Debug-level line naming the
        // call — Debug rather than Warning because a timeout is routine, unlike the missing-function
        // case below. The clicked option must still show as selected either way, same reasoning as the
        // tests above: applyTheme() never ran, so reverting would just fight the user's click.
        var logs = new RecordingLoggerProvider();
        using var ctx = NewContext(logs);
        ctx.JSInterop.Setup<string>("sqlAgentUi.getTheme").SetResult("system");
        ctx.JSInterop.SetupVoid("sqlAgentUi.setTheme", _ => true)
            .SetException(new TaskCanceledException("JS interop call timed out"));
        var toggle = ctx.RenderComponent<ThemeToggle>();

        toggle.FindAll("button").Single(b => b.TextContent.Contains("Dark")).Click();

        Assert.Equal("true", toggle.FindAll("button")
            .Single(b => b.TextContent.Contains("Dark")).GetAttribute("aria-pressed"));
        var record = logs.Records.Single(r => r.Message.Contains("sqlAgentUi.setTheme failed"));
        Assert.Equal(LogLevel.Debug, record.Level);
        Assert.IsAssignableFrom<OperationCanceledException>(record.Exception);
    }

    [Fact]
    public void A_missing_interop_function_is_logged_loudly_but_a_gone_circuit_is_not()
    {
        // This component's own comment says the point of logging these at all is that "a renamed/missing
        // interop call is a real bug that would otherwise leave the control silently and permanently
        // wrong with zero evidence in any log" — and at Debug it produced exactly zero evidence, because
        // appsettings.json sets Logging:LogLevel:Default to Information with no Development override.
        // The split is by meaning: a JSException is sqlAgentUi.setTheme missing, renamed, or throwing, a
        // real bug, so it clears the Information default at Warning. A dropped circuit is routine and
        // stays at Debug, where it cannot drown the log every time a laptop lid closes.
        var bug = new RecordingLoggerProvider();
        using (var ctx = NewContext(bug))
        {
            ctx.JSInterop.Setup<string>("sqlAgentUi.getTheme").SetResult("system");
            ctx.JSInterop.SetupVoid("sqlAgentUi.setTheme", _ => true)
                .SetException(new JSException("Could not find 'sqlAgentUi.setTheme'"));
            ctx.RenderComponent<ThemeToggle>()
                .FindAll("button").Single(b => b.TextContent.Contains("Dark")).Click();
        }

        Assert.Equal(LogLevel.Warning,
            bug.Records.Single(r => r.Message.Contains("sqlAgentUi.setTheme failed")).Level);

        var routine = new RecordingLoggerProvider();
        using (var ctx = NewContext(routine))
        {
            ctx.JSInterop.Setup<string>("sqlAgentUi.getTheme").SetResult("system");
            ctx.JSInterop.SetupVoid("sqlAgentUi.setTheme", _ => true)
                .SetException(new JSDisconnectedException("circuit gone"));
            ctx.RenderComponent<ThemeToggle>()
                .FindAll("button").Single(b => b.TextContent.Contains("Dark")).Click();
        }

        Assert.Equal(LogLevel.Debug,
            routine.Records.Single(r => r.Message.Contains("sqlAgentUi.setTheme failed")).Level);
    }

    [Fact]
    public void A_missing_getTheme_on_first_render_is_logged_loudly_too()
    {
        // The read path's catch is broad by design (private-mode localStorage can throw arbitrary
        // types), so the level split is the only thing that separates "this browser won't tell us the
        // theme" from "our own interop contract is broken". Without it, the read path is where a renamed
        // sqlAgentUi.getTheme hides most completely: the control renders a perfectly normal-looking
        // "System" and nothing suggests it never actually asked.
        var logs = new RecordingLoggerProvider();
        using var ctx = NewContext(logs);
        ctx.JSInterop.Mode = JSRuntimeMode.Strict;
        ctx.JSInterop.Setup<string>("sqlAgentUi.getTheme")
            .SetException(new JSException("Could not find 'sqlAgentUi.getTheme'"));

        ctx.RenderComponent<ThemeToggle>();

        Assert.Equal(LogLevel.Warning,
            logs.Records.Single(r => r.Message.Contains("sqlAgentUi.getTheme failed")).Level);
    }
}
