using Bunit;
using SqlAgent.Host.Components.Shared.Ui;

namespace SqlAgent.Tests;

public class UiPrimitiveTests
{
    [Fact]
    public void An_icon_renders_an_svg_with_the_requested_size()
    {
        using var ctx = new Bunit.TestContext();

        var icon = ctx.RenderComponent<Icon>(p => p.Add(i => i.Name, "database").Add(i => i.Size, 16));

        var svg = icon.Find("svg");
        Assert.Equal("16", svg.GetAttribute("width"));
        Assert.Equal("16", svg.GetAttribute("height"));
        Assert.NotEmpty(icon.FindAll("svg path"));
    }

    [Fact]
    public void An_icon_inherits_the_surrounding_text_color()
    {
        // Icons sit inside buttons and menu rows whose color changes on hover and between themes.
        // A hard-coded stroke would strand them at one color in one theme.
        using var ctx = new Bunit.TestContext();

        var icon = ctx.RenderComponent<Icon>(p => p.Add(i => i.Name, "database"));

        Assert.Equal("currentColor", icon.Find("svg").GetAttribute("stroke"));
    }

    [Fact]
    public void An_unknown_icon_name_renders_nothing_rather_than_throwing()
    {
        // A typo'd icon name must degrade to a blank space, not take out the whole page through
        // WorkArea's error boundary.
        using var ctx = new Bunit.TestContext();

        var icon = ctx.RenderComponent<Icon>(p => p.Add(i => i.Name, "definitely-not-an-icon"));

        Assert.Empty(icon.FindAll("svg"));
    }

    [Theory]
    [InlineData("panel-left")]
    [InlineData("menu")]
    [InlineData("sun")]
    [InlineData("moon")]
    [InlineData("monitor")]
    [InlineData("settings")]
    [InlineData("info")]
    [InlineData("database")]
    [InlineData("message-square")]
    [InlineData("chevron-down")]
    [InlineData("x")]
    public void The_icons_the_shell_needs_all_exist(string name)
    {
        // The shell references these by string, so a missing one is invisible until someone opens the
        // page it is on. Enumerating them here turns that into a build-time failure.
        Assert.Contains(name, Icon.Names);
    }

    [Fact]
    public void No_icon_ships_that_nothing_renders()
    {
        // Phase A ships only the glyphs Phase A draws. Phases B-D add theirs alongside the components
        // that render them, so an unused glyph never sits in the set waiting for a caller that a later
        // phase might rename or never write.
        var rendered = new[]
        {
            "panel-left", "menu", "sun", "moon", "monitor", "settings",
            "info", "database", "message-square", "chevron-down", "x",
        };

        Assert.Equal(rendered.OrderBy(n => n, StringComparer.Ordinal), Icon.Names.OrderBy(n => n, StringComparer.Ordinal));
    }

    [Fact]
    public void A_badge_renders_its_content_and_carries_its_tone_as_a_class()
    {
        using var ctx = new Bunit.TestContext();

        var badge = ctx.RenderComponent<Badge>(p => p
            .Add(b => b.Tone, BadgeTone.Success)
            .AddChildContent("connected"));

        Assert.Contains("connected", badge.Markup);
        Assert.Contains("success", badge.Find("span").ClassName);
    }

    [Fact]
    public void A_spinner_announces_itself_to_assistive_technology()
    {
        using var ctx = new Bunit.TestContext();

        var spinner = ctx.RenderComponent<Spinner>(p => p.Add(s => s.Label, "Running query"));

        Assert.Equal("status", spinner.Find("[role]").GetAttribute("role"));
        Assert.Contains("Running query", spinner.Markup);
    }

    [Fact]
    public void An_empty_state_renders_its_title_hint_and_actions()
    {
        using var ctx = new Bunit.TestContext();

        var empty = ctx.RenderComponent<EmptyState>(p => p
            .Add(e => e.Icon, "database")
            .Add(e => e.Title, "No databases yet")
            .Add(e => e.Hint, "Add one to get started")
            .AddChildContent("<button>Add database</button>"));

        Assert.Contains("No databases yet", empty.Markup);
        Assert.Contains("Add one to get started", empty.Markup);
        Assert.Contains("Add database", empty.Find("button").TextContent);
    }
}
