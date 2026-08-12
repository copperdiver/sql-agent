using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using SqlAgent.Host.Components.Shared.Ui;

namespace SqlAgent.Tests;

public class ThemeToggleTests
{
    // ThemeToggle injects ILogger<ThemeToggle> (both interop catches log at Debug), so every test needs
    // logging registered or bUnit's DI container fails to resolve the component at all.
    private static Bunit.TestContext NewContext()
    {
        var ctx = new Bunit.TestContext();
        ctx.Services.AddLogging();
        return ctx;
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
        // below triggers must be planned or it throws JSRuntimeUnhandledInvocationException — which
        // ApplyAsync's catch (JSException only, by design) does not swallow.
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
}
