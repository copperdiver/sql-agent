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
    public void A_click_inside_the_modal_panel_does_not_close_it()
    {
        // The scrim and the panel are nested, so without stopPropagation every click on the dialog's
        // own content would bubble to the scrim's handler and dismiss the dialog mid-interaction.
        using var ctx = new Bunit.TestContext();
        var closes = 0;
        var modal = ctx.RenderComponent<Modal>(p => p
            .Add(m => m.Title, "t")
            .Add(m => m.OnClose, EventCallback.Factory.Create(new object(), () => closes++))
            .AddChildContent("<p id=\"inside\">body</p>"));

        modal.Find("#inside").Click();

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
