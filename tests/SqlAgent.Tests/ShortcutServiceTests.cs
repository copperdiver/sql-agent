using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using SqlAgent.Host.Components.Shared.Ui;
using SqlAgent.Host.Web;

namespace SqlAgent.Tests;

/// <summary>
/// The service exists so one document-level listener can reach whatever is open. bUnit runs no browser,
/// so the listener itself is a manual check; what is testable here — and what actually broke in Safari —
/// is whether an open popover is listening and a closed one is not.
/// </summary>
public class ShortcutServiceTests
{
    [Fact]
    public void An_open_menu_closes_on_a_global_escape()
    {
        // Safari does not focus a button on a plain mouse click, so Menu's own keydown handler never
        // fires there: the keypress goes to the document and nothing in the menu hears it.
        using var ctx = NewContext(out var shortcuts);
        var menu = ctx.RenderComponent<Menu>(p => p
            .Add(m => m.Trigger, (RenderFragment)(b => b.AddMarkupContent(0, "<span>t</span>")))
            .AddChildContent("<div id=\"body\">contents</div>"));
        menu.Find(".menu-trigger").Click();
        Assert.Single(menu.FindAll("#body"));

        menu.InvokeAsync(shortcuts.RaiseEscape);

        Assert.Empty(menu.FindAll("#body"));
    }

    [Fact]
    public void A_closed_menu_is_not_listening()
    {
        // Every menu in the sidebar would otherwise hold a subscription for the life of the circuit, and
        // a global Escape would run one handler per row. The same conditional-attachment discipline
        // Phase A applied to the drawer's keydown handler.
        using var ctx = NewContext(out var shortcuts);
        var menu = ctx.RenderComponent<Menu>(p => p
            .Add(m => m.Trigger, (RenderFragment)(b => b.AddMarkupContent(0, "<span>t</span>")))
            .AddChildContent("<div id=\"body\">contents</div>"));

        menu.Find(".menu-trigger").Click();
        menu.Find(".menu-backdrop").Click();
        Assert.Empty(menu.FindAll("#body"));

        // Raising it again must not reopen anything or throw into a component that stopped listening.
        menu.InvokeAsync(shortcuts.RaiseEscape);

        Assert.Empty(menu.FindAll("#body"));
    }

    [Fact]
    public void An_open_modal_closes_on_a_global_escape()
    {
        using var ctx = NewContext(out var shortcuts);
        var closes = 0;
        var modal = ctx.RenderComponent<Modal>(p => p
            .Add(m => m.Title, "About")
            .Add(m => m.OnClose, EventCallback.Factory.Create(new object(), () => closes++))
            .AddChildContent("<p>body</p>"));

        modal.InvokeAsync(shortcuts.RaiseEscape);

        Assert.Equal(1, closes);
    }

    [Fact]
    public void A_disposed_component_stops_listening()
    {
        // A leaked subscription keeps a torn-down component alive for the rest of the circuit — the same
        // leak ShellTests pins for Sidebar's LocationChanged handler.
        using var ctx = NewContext(out var shortcuts);
        var closes = 0;
        ctx.RenderComponent<Modal>(p => p
            .Add(m => m.Title, "About")
            .Add(m => m.OnClose, EventCallback.Factory.Create(new object(), () => closes++))
            .AddChildContent("<p>body</p>"));

        ctx.DisposeComponents();
        shortcuts.RaiseEscape();

        Assert.Equal(0, closes);
    }

    [Fact]
    public void The_search_request_reaches_whoever_is_listening()
    {
        var shortcuts = new ShortcutService();
        var asked = 0;
        shortcuts.SearchRequested += () => asked++;

        shortcuts.RaiseSearch();

        Assert.Equal(1, asked);
    }

    // --- Final whole-branch review: shortcuts.js source pinning ------------------------------
    //
    // bUnit runs no browser and no JS engine at all, so nothing above can exercise shortcuts.js itself —
    // only the C# side of the interop boundary it feeds ([JSInvokable] OnEscape/OnSearch). These two pin
    // the source text directly, the same way ShellTests pins Sidebar.razor.css and app.css facts bUnit
    // has no other way to see.

    [Fact]
    public void The_bind_function_unbinds_any_previous_listener_before_attaching_a_new_one()
    {
        // A circuit can reconnect and call bind() again without DisposeAsync ever having called unbind()
        // first (that call races the reconnect and can lose) — without this, the old listener stays
        // attached to a dead DotNetObjectReference forever, and every keystroke after that invokes
        // OnEscape/OnSearch on both the dead ref and the live one.
        var js = File.ReadAllText(RepoPaths.Find("src/SqlAgent.Host/wwwroot/js/shortcuts.js"));
        var bindStart = js.IndexOf("bind: function (dotNetRef) {", StringComparison.Ordinal);
        var unbindStart = js.IndexOf("unbind: function () {", StringComparison.Ordinal);
        Assert.True(bindStart >= 0 && unbindStart > bindStart,
            "Expected to find bind() defined before unbind() in shortcuts.js.");
        var bindBody = js[bindStart..unbindStart];

        Assert.Contains("this.unbind();", bindBody);
        // Positional, not just present: called after the new listener is attached would still remove it
        // immediately, leaving nothing bound at all.
        Assert.True(
            bindBody.IndexOf("this.unbind();", StringComparison.Ordinal) <
            bindBody.IndexOf("addEventListener", StringComparison.Ordinal),
            "Expected unbind() to run before the new listener is attached.");
    }

    [Fact]
    public void The_search_shortcut_match_excludes_shift_and_alt()
    {
        // Plain e.key === 'k'/'K' also fired on Ctrl/Cmd+Shift+K (Chrome/Firefox's own "reopen closed
        // tab") and on every AltGr-shifted character on a European Windows keyboard layout, where AltGr
        // reports as ctrlKey + altKey both true rather than as a distinct modifier.
        var js = File.ReadAllText(RepoPaths.Find("src/SqlAgent.Host/wwwroot/js/shortcuts.js"));

        Assert.Contains("!e.shiftKey && !e.altKey && e.key.toLowerCase() === 'k'", js);
    }

    private static Bunit.TestContext NewContext(out ShortcutService shortcuts)
    {
        var ctx = new Bunit.TestContext();
        shortcuts = new ShortcutService();
        ctx.Services.AddSingleton(shortcuts);
        // Modal now moves focus explicitly on open (a real JS interop call, replacing the autofocus
        // attribute it used to render) rather than something bUnit's default strict interop mode allows
        // through unconfigured.
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        return ctx;
    }
}
