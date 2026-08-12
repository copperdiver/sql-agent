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

        menu.Find(".menu-item-action").Click();

        Assert.True(clicked);
        Assert.Empty(menu.FindAll(".menu-item"));
    }

    [Fact]
    public void Setting_CloseOnClick_false_keeps_the_menu_open_after_activating()
    {
        // A row whose own button has no real effect -- e.g. a label wrapping a Trailing widget that
        // does the actual work, like UserCard's Theme row -- should not dismiss the menu just because
        // the user's click landed on the label rather than the widget. CloseOnClick=false is how that
        // row opts out of MenuItem's default close-on-activate behavior.
        using var ctx = new Bunit.TestContext();
        var clicked = false;
        var menu = ctx.RenderComponent<Menu>(p => p
            .Add(m => m.Trigger, (RenderFragment)(b => b.AddMarkupContent(0, "<span>t</span>")))
            .AddChildContent<MenuItem>(ip => ip
                .Add(i => i.CloseOnClick, false)
                .Add(i => i.OnClick, EventCallback.Factory.Create(new object(), () => clicked = true))
                .AddChildContent("Theme")));
        menu.Find(".menu-trigger").Click();

        menu.Find(".menu-item-action").Click();

        Assert.True(clicked);
        Assert.Single(menu.FindAll(".menu-item"));
    }

    [Fact]
    public void A_menu_item_does_not_nest_a_button_inside_its_trailing_slot()
    {
        // MenuItem used to render icon + label + Trailing all inside one <button>. Task 6 puts a
        // Segmented control (itself a row of <button>s) in Trailing, and a button inside a button is
        // invalid HTML — the parser silently closes the outer one, fragmenting the row and moving the
        // trailing content outside the element meant to contain it. bUnit's renderer does not warn
        // about this, so this test has to look at the actual element structure.
        using var ctx = new Bunit.TestContext();
        var menu = ctx.RenderComponent<Menu>(p => p
            .Add(m => m.Trigger, (RenderFragment)(b => b.AddMarkupContent(0, "<span>t</span>")))
            .AddChildContent<MenuItem>(ip => ip
                .AddChildContent("Theme")
                .Add(i => i.Trailing, (RenderFragment)(b => b.AddMarkupContent(0, "<button>toggle</button>")))));
        menu.Find(".menu-trigger").Click();

        Assert.Empty(menu.FindAll("button button"));

        var trailingButton = menu.Find(".menu-item-trailing button");
        Assert.Null(trailingButton.Closest(".menu-item-action"));
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
    public void The_modal_panel_is_a_sibling_of_the_scrim_so_panel_clicks_cannot_dismiss_it()
    {
        // It's the shape of the DOM, not any click-handling logic, that keeps a click inside the panel
        // from dismissing the dialog: the scrim and the panel are siblings under .modal-root, and the
        // panel simply paints above the scrim (z-index), so a click inside the panel hit-tests to the
        // panel and never reaches the scrim's Close handler at all. That is worth pinning directly
        // rather than through a click-and-observe test — asserting on bUnit's own handler-resolution
        // exception (the previous version of this test) is a statement about bUnit's internals, not
        // about this component, and would break for reasons unrelated to the regression it exists to
        // catch. If someone later nests the panel inside the scrim, this fails immediately.
        using var ctx = new Bunit.TestContext();
        var modal = ctx.RenderComponent<Modal>(p => p
            .Add(m => m.Title, "t")
            .AddChildContent("<p>body</p>"));

        var scrim = modal.Find(".modal-scrim");
        var panel = modal.Find(".modal-panel");

        Assert.Same(scrim.ParentElement, panel.ParentElement);
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
