using Bunit;
using Microsoft.AspNetCore.Components;
using SqlAgent.Host.Components.Shared.Ui;

namespace SqlAgent.Tests;

public class UiInteractionTests
{
    [Fact]
    public void A_menu_is_closed_until_its_trigger_is_clicked()
    {
        using var ctx = new Bunit.TestContext();

        var menu = ctx.RenderComponent<Menu>(p => p
            .Add(m => m.Trigger, (RenderFragment)(b => b.AddMarkupContent(0, "<span>open me</span>")))
            .AddChildContent("<div id=\"body\">contents</div>"));

        Assert.Empty(menu.FindAll("#body"));

        menu.Find(".menu-trigger").Click();

        Assert.Single(menu.FindAll("#body"));
    }

    [Fact]
    public void The_menu_trigger_is_a_focusable_button_so_a_real_Escape_keypress_can_reach_it()
    {
        // .menu-root's Escape handler only fires via bubbling from whatever element currently has
        // focus. KeyDown() below invokes it directly and would pass even if nothing were focusable, so
        // it cannot catch a regression to a plain <div> trigger — this test pins the tag name instead.
        using var ctx = new Bunit.TestContext();
        var menu = ctx.RenderComponent<Menu>(p => p
            .Add(m => m.Trigger, (RenderFragment)(b => b.AddMarkupContent(0, "<span>t</span>")))
            .AddChildContent("<div id=\"body\">contents</div>"));

        Assert.Equal("button", menu.Find(".menu-trigger").TagName.ToLowerInvariant());
    }

    [Fact]
    public void Clicking_the_backdrop_closes_the_menu()
    {
        // Without a backdrop the only way out of an open menu is re-clicking the trigger, which is not
        // how any menu on any platform behaves. It is a plain element rather than a document-level JS
        // listener so it works in the static first render too.
        using var ctx = new Bunit.TestContext();
        var menu = ctx.RenderComponent<Menu>(p => p
            .Add(m => m.Trigger, (RenderFragment)(b => b.AddMarkupContent(0, "<span>t</span>")))
            .AddChildContent("<div id=\"body\">contents</div>"));
        menu.Find(".menu-trigger").Click();

        menu.Find(".menu-backdrop").Click();

        Assert.Empty(menu.FindAll("#body"));
    }

    [Fact]
    public void Escape_closes_the_menu()
    {
        using var ctx = new Bunit.TestContext();
        var menu = ctx.RenderComponent<Menu>(p => p
            .Add(m => m.Trigger, (RenderFragment)(b => b.AddMarkupContent(0, "<span>t</span>")))
            .AddChildContent("<div id=\"body\">contents</div>"));
        menu.Find(".menu-trigger").Click();

        menu.Find(".menu-root").KeyDown(Key.Escape);

        Assert.Empty(menu.FindAll("#body"));
    }

    [Fact]
    public void Choosing_a_menu_item_invokes_its_callback_and_closes_the_menu()
    {
        using var ctx = new Bunit.TestContext();
        var clicked = false;
        var menu = ctx.RenderComponent<Menu>(p => p
            .Add(m => m.Trigger, (RenderFragment)(b => b.AddMarkupContent(0, "<span>t</span>")))
            .AddChildContent<MenuItem>(ip => ip
                .Add(i => i.OnClick, EventCallback.Factory.Create(new object(), () => clicked = true))
                .AddChildContent("Settings")));
        menu.Find(".menu-trigger").Click();

        menu.Find(".menu-item").Click();

        Assert.True(clicked);
        Assert.Empty(menu.FindAll(".menu-item"));
    }

    [Fact]
    public void A_segmented_control_marks_the_selected_option_and_reports_changes()
    {
        using var ctx = new Bunit.TestContext();
        var chosen = "system";
        var segmented = ctx.RenderComponent<Segmented>(p => p
            .Add(s => s.Options, new List<SegmentedOption>
            {
                new("system", "System", "monitor"),
                new("light", "Light", "sun"),
                new("dark", "Dark", "moon"),
            })
            .Add(s => s.Value, chosen)
            .Add(s => s.ValueChanged, EventCallback.Factory.Create<string>(new object(), v => chosen = v)));

        var buttons = segmented.FindAll("button");
        Assert.Equal(3, buttons.Count);
        Assert.Equal("true", buttons[0].GetAttribute("aria-pressed"));

        buttons[2].Click();

        Assert.Equal("dark", chosen);
    }

    [Fact]
    public void A_modal_renders_its_title_and_closes_on_escape_and_on_the_scrim()
    {
        using var ctx = new Bunit.TestContext();
        var closes = 0;
        var modal = ctx.RenderComponent<Modal>(p => p
            .Add(m => m.Title, "About SQL Agent")
            .Add(m => m.OnClose, EventCallback.Factory.Create(new object(), () => closes++))
            .AddChildContent("<p>body</p>"));

        Assert.Contains("About SQL Agent", modal.Markup);
        Assert.Equal("dialog", modal.Find("[role]").GetAttribute("role"));

        modal.Find(".modal-scrim").Click();
        modal.Find(".modal-root").KeyDown(Key.Escape);

        Assert.Equal(2, closes);
    }

    [Fact]
    public void The_modal_close_button_autofocuses_so_a_real_Escape_keypress_can_reach_the_dialog()
    {
        // .modal-root's Escape handler only fires via bubbling from whatever element currently has
        // focus. KeyDown() above invokes it directly and would pass even if focus never moved into the
        // dialog, so it cannot catch a regression here — this test pins the autofocus attribute that
        // is what actually gets a real Escape keypress to bubble from inside .modal-root at all.
        using var ctx = new Bunit.TestContext();
        var modal = ctx.RenderComponent<Modal>(p => p
            .Add(m => m.Title, "t")
            .AddChildContent("<p>body</p>"));

        Assert.True(modal.Find(".modal-close").HasAttribute("autofocus"));
    }

    [Fact]
    public void A_click_inside_the_modal_panel_does_not_close_it()
    {
        // The scrim and the panel are SIBLINGS under .modal-root (the panel simply paints above via
        // z-index), not nested, so a click inside the panel hit-tests to the panel and can never reach
        // the scrim's Close handler — there is no handler anywhere in #inside's ancestry to catch it,
        // which is exactly why bUnit reports that as MissingEventHandlerException rather than routing
        // the click anywhere. That exception is itself the proof nothing closed the dialog. This
        // guards against a future regression where someone nests the panel inside the scrim: a click
        // would then find the scrim's Close handler in its ancestry, no exception would be thrown, and
        // closes would become 1 — failing this test either way.
        using var ctx = new Bunit.TestContext();
        var closes = 0;
        var modal = ctx.RenderComponent<Modal>(p => p
            .Add(m => m.Title, "t")
            .Add(m => m.OnClose, EventCallback.Factory.Create(new object(), () => closes++))
            .AddChildContent("<p id=\"inside\">body</p>"));

        Assert.Throws<MissingEventHandlerException>(() => modal.Find("#inside").Click());

        Assert.Equal(0, closes);
    }

    [Fact]
    public void A_modal_footer_is_rendered_only_when_supplied()
    {
        // The footer is the slot Phase D's confirm dialog will fill. It must be genuinely optional, or
        // every plain modal (About, for one) grows an empty bordered strip.
        using var ctx = new Bunit.TestContext();

        var plain = ctx.RenderComponent<Modal>(p => p
            .Add(m => m.Title, "About")
            .AddChildContent("<p>body</p>"));
        Assert.Empty(plain.FindAll(".modal-foot"));

        var withFooter = ctx.RenderComponent<Modal>(p => p
            .Add(m => m.Title, "About")
            .Add(m => m.Footer, (RenderFragment)(b => b.AddMarkupContent(0, "<button>OK</button>")))
            .AddChildContent("<p>body</p>"));
        Assert.Single(withFooter.FindAll(".modal-foot"));
    }
}
