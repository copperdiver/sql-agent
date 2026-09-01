using System.Text.RegularExpressions;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using SqlAgent.Host.Components.Shared.Ui;
using SqlAgent.Host.Web;

namespace SqlAgent.Tests;

public class UiPrimitiveTests
{
    [Fact]
    public void A_modal_focuses_once_on_open_and_again_only_when_its_signal_changes()
    {
        // bUnit cannot observe document.activeElement, so what is asserted here is the decision to
        // focus — how many times the dialog asks for its target. Whether the browser honours it is a
        // manual-checklist row, and always will be.
        using var ctx = new Bunit.BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddScoped<ShortcutService>();

        var asked = 0;
        ElementReference captured = default;
        var modal = ctx.Render<Modal>(p => p
            .Add(m => m.Title, "t")
            .Add(m => m.FocusSignal, 0)
            .Add(m => m.ChildContent, (RenderFragment)(b =>
            {
                b.OpenElement(0, "input");
                b.AddElementReferenceCapture(1, r => captured = r);
                b.CloseElement();
            }))
            .Add(m => m.InitialFocus, () => { asked++; return captured; }));

        Assert.Equal(1, asked);

        modal.Render(p => p.Add(m => m.FocusSignal, 0));
        Assert.Equal(1, asked);

        modal.Render(p => p.Add(m => m.FocusSignal, 1));
        Assert.Equal(2, asked);
    }

    [Fact]
    public void An_icon_renders_an_svg_with_the_requested_size()
    {
        using var ctx = new Bunit.BunitContext();

        var icon = ctx.Render<Icon>(p => p.Add(i => i.Name, "database").Add(i => i.Size, 16));

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
        using var ctx = new Bunit.BunitContext();

        var icon = ctx.Render<Icon>(p => p.Add(i => i.Name, "database"));

        Assert.Equal("currentColor", icon.Find("svg").GetAttribute("stroke"));
    }

    [Fact]
    public void An_unknown_icon_name_renders_nothing_rather_than_throwing()
    {
        // A typo'd icon name must degrade to a blank space, not take out the whole page through
        // WorkArea's error boundary.
        using var ctx = new Bunit.BunitContext();

        var icon = ctx.Render<Icon>(p => p.Add(i => i.Name, "definitely-not-an-icon"));

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
    [InlineData("chevron-down")]
    [InlineData("x")]
    [InlineData("plus")]
    [InlineData("terminal")]
    [InlineData("paperclip")]
    [InlineData("more-vertical")]
    [InlineData("pencil")]
    [InlineData("trash")]
    [InlineData("arrow-up")]
    [InlineData("square")]
    [InlineData("folder")]
    [InlineData("chevron-right")]
    [InlineData("search")]
    public void The_icons_the_shell_needs_all_exist(string name)
    {
        // The shell references these by string, so a missing one is invisible until someone opens the
        // page it is on. Enumerating them here turns that into a build-time failure.
        Assert.Contains(name, Icon.Names);
    }

    [Fact]
    public void No_icon_ships_that_nothing_renders()
    {
        // Each phase ships only the glyphs it draws, so an unused glyph never sits in the set waiting
        // for a caller a later phase might rename or never write.
        //
        // This used to compare Icon.Names against a hardcoded list, which made it a change-detector
        // wearing a policy's name: adding a glyph and adding its name to the list satisfied it while
        // nothing rendered the glyph — which is exactly how "message-square" shipped and sat unused
        // until this rewrite caught it. It now reads the markup, so the only way to pass is to render it.
        //
        // Three shapes carry a glyph name in this codebase, and all three are plain literals rather than
        // expressions, so a regex still suffices without becoming an expression parser:
        //  - `<Icon Name="…">` directly (Task 5's project chevron is two elements under an @if rather
        //    than one with a ternary name, precisely so this stays matchable);
        //  - `Icon="…"` on MenuItem/EmptyState, which forward it to their own inner `<Icon Name="@Icon">`;
        //  - the third positional string in ThemeToggle's `new("value", "Label", "icon-name")` options —
        //    Segmented renders each as `<Icon Name="@option.Icon">`.
        var componentsRoot = Path.GetDirectoryName(RepoPaths.Find("src/SqlAgent.Host/Components/App.razor"))!;

        var rendered = Directory
            .EnumerateFiles(componentsRoot, "*.razor", SearchOption.AllDirectories)
            .Select(File.ReadAllText)
            .SelectMany(text => Regex.Matches(text, @"<Icon\b[^>]*?\bName\s*=\s*""([a-z0-9-]+)""")
                .Concat(Regex.Matches(text, @"\bIcon\s*=\s*""([a-z0-9-]+)"""))
                .Concat(Regex.Matches(text, @"new\(\s*""[a-z0-9-]+""\s*,\s*""[^""]*""\s*,\s*""([a-z0-9-]+)""\s*\)")))
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(rendered);
        Assert.Equal(
            rendered.OrderBy(n => n, StringComparer.Ordinal),
            Icon.Names.OrderBy(n => n, StringComparer.Ordinal));
    }

    [Fact]
    public void A_badge_renders_its_content_and_carries_its_tone_as_a_class()
    {
        using var ctx = new Bunit.BunitContext();

        var badge = ctx.Render<Badge>(p => p
            .Add(b => b.Tone, BadgeTone.Success)
            .AddChildContent("connected"));

        Assert.Contains("connected", badge.Markup);
        Assert.Contains("success", badge.Find("span").ClassName);
    }

    [Fact]
    public void A_spinner_announces_itself_to_assistive_technology()
    {
        using var ctx = new Bunit.BunitContext();

        var spinner = ctx.Render<Spinner>(p => p.Add(s => s.Label, "Running query"));

        Assert.Equal("status", spinner.Find("[role]").GetAttribute("role"));
        Assert.Contains("Running query", spinner.Markup);
    }

    [Fact]
    public void An_empty_state_renders_its_title_hint_and_actions()
    {
        using var ctx = new Bunit.BunitContext();

        var empty = ctx.Render<EmptyState>(p => p
            .Add(e => e.Icon, "database")
            .Add(e => e.Title, "No databases yet")
            .Add(e => e.Hint, "Add one to get started")
            .AddChildContent("<button>Add database</button>"));

        Assert.Contains("No databases yet", empty.Markup);
        Assert.Contains("Add one to get started", empty.Markup);
        Assert.Contains("Add database", empty.Find("button").TextContent);
    }
}
