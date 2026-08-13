using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
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

    private static Bunit.TestContext NewContext(out ShortcutService shortcuts)
    {
        var ctx = new Bunit.TestContext();
        shortcuts = new ShortcutService();
        ctx.Services.AddSingleton(shortcuts);
        return ctx;
    }
}
