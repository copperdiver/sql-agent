using System.Net;
using System.Text.RegularExpressions;

namespace SqlAgent.Tests;

/// <summary>Resolves repo-relative paths from the test assembly's location, so tests can assert on
/// source files (CSS, JS) that are not compiled into the assembly.</summary>
public static class RepoPaths
{
    public static string Find(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException($"Could not find '{relativePath}' in any ancestor of {AppContext.BaseDirectory}.");
    }
}

public class DesignSystemTests : IClassFixture<WebTestHost>
{
    private readonly WebTestHost _host;
    public DesignSystemTests(WebTestHost host) => _host = host;

    private static string Css() => File.ReadAllText(RepoPaths.Find("src/SqlAgent.Host/wwwroot/css/app.css"));

    [Fact]
    public async Task The_stylesheet_is_served()
    {
        // Mirrors Framework_assets_are_reachable_without_a_token: asserting only "not 401" would let a
        // 404 pass, and a stylesheet that 404s is exactly how this UI shipped unstyled before.
        var client = _host.NewClient();
        await client.GetAsync($"/?token={WebTestHost.Token}");

        var r = await client.GetAsync("/css/app.css");

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.NotEmpty(await r.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_theme_script_is_served()
    {
        var client = _host.NewClient();
        await client.GetAsync($"/?token={WebTestHost.Token}");

        var r = await client.GetAsync("/js/theme.js");

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.NotEmpty(await r.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_document_links_the_stylesheet_and_loads_the_theme_script_in_head()
    {
        // The script must be in <head> and synchronous: loaded from <body>, or deferred, the browser
        // paints the light theme first and the user sees a flash before the dark class lands.
        var client = _host.NewClient();
        var html = await client.GetStringAsync($"/?token={WebTestHost.Token}");

        var head = html[..html.IndexOf("</head>", StringComparison.OrdinalIgnoreCase)];

        Assert.Contains("css/app.css", head);
        Assert.Contains("SqlAgent.Host.styles.css", head);
        Assert.Contains("js/theme.js", head);
        Assert.DoesNotContain("defer", head);
        Assert.DoesNotContain("async", head);
    }

    [Fact]
    public void Every_token_redefined_for_dark_mode_is_also_redefined_for_the_system_preference()
    {
        // The dark palette is written twice — once for :root.dark (explicit choice) and once inside
        // @media (prefers-color-scheme: dark) for the "system" setting, which sets no class. Two copies
        // can drift, and drift shows up as one or two stray light-mode colors on a dark page, which is
        // easy to miss by eye. This pins them to the same property set.
        var css = Css();

        var explicitDarkBlock = Block(css, ":root.dark");
        var systemDarkBlock = Block(css, ":root:not(.light):not(.dark)");

        var explicitDark = Properties(explicitDarkBlock);
        var systemDark = Properties(systemDarkBlock);

        Assert.NotEmpty(explicitDark);
        Assert.Equal(explicitDark, systemDark);

        // color-scheme is not a custom property, so the name comparison above only sees it because
        // Properties() is widened to match it explicitly — and the name matching is still not enough on
        // its own, since "color-scheme: light" in one block and "dark" in the other would satisfy it.
        // Getting this wrong is not a subtle drift: color-scheme is what tells the browser to paint
        // native controls, form widgets and scrollbars dark, so losing it in the @media block leaves
        // every system-theme user with light-on-dark chrome over a dark page.
        Assert.Contains("color-scheme: dark", explicitDarkBlock);
        Assert.Contains("color-scheme: dark", systemDarkBlock);
    }

    [Fact]
    public void The_stylesheet_restores_a_focus_indicator_for_checkboxes_and_radios()
    {
        // app.css sets "outline: none" on input:focus and compensates with a border-color swap. That
        // substitute works for a text input or a select, but a native checkbox under the default
        // `appearance: auto` paints its own widget and ignores author border, background and padding
        // entirely — so the compensation renders nothing and the focus ring is simply gone (WCAG 2.4.7).
        // The generic ":focus-visible" rule earlier in the file cannot cover it either: "input:focus" is
        // (0,1,1) against its (0,1,0) and comes later in source order. bUnit runs no browser and no CSS
        // engine, so specificity and cascade cannot be observed from a rendered component at all; this
        // is pinned on the stylesheet source, the same way the rest of the app.css facts here are.
        var css = Css();

        var rule = Regex.Match(css,
            @"input\[type=checkbox\]:focus-visible[^{]*\{([^}]*)\}");

        Assert.True(rule.Success,
            "app.css must carry an input[type=checkbox]:focus-visible rule; the generic input:focus rule removes the outline.");
        Assert.Contains("outline:", rule.Groups[1].Value);
        Assert.DoesNotContain("outline: none", rule.Groups[1].Value);
        Assert.Contains("input[type=radio]:focus-visible", rule.Value);
    }

    [Fact]
    public void The_vendored_font_ships_its_license_and_copyright_notice()
    {
        // OFL 1.1 condition 2 permits redistribution only if "each copy contains the above copyright
        // notice and this license". The woff2 is redistributed here, so both have to travel with it —
        // a README merely naming the OFL is not the license text. This is a File.Exists assertion
        // rather than a docs note so that moving or pruning wwwroot/fonts fails the build instead of
        // silently shipping an unlicensed binary.
        var font = RepoPaths.Find("src/SqlAgent.Host/wwwroot/fonts/DMSans-Variable.woff2");
        var license = RepoPaths.Find("src/SqlAgent.Host/wwwroot/fonts/OFL.txt");

        Assert.True(File.Exists(font));
        Assert.True(File.Exists(license));

        var text = File.ReadAllText(license);
        Assert.Contains("SIL OPEN FONT LICENSE Version 1.1", text);
        Assert.Contains("The DM Sans Project Authors", text);

        // The copyright notice must also be reproduced where a reader looks first, not only inside the
        // license file's own header.
        var readme = File.ReadAllText(RepoPaths.Find("src/SqlAgent.Host/wwwroot/fonts/README.md"));
        Assert.Contains("The DM Sans Project Authors", readme);
    }

    [Fact]
    public void The_font_stack_falls_back_to_system_fonts()
    {
        // DM Sans is vendored, but a deployment that loses the woff2 (or a build that never fetched it)
        // must still render in a sane sans-serif rather than the browser's serif default.
        var css = Css();

        var match = Regex.Match(css, @"--font-sans:\s*([^;]+);");

        Assert.True(match.Success, "app.css must define --font-sans");
        Assert.Contains("system-ui", match.Groups[1].Value);
    }

    /// <summary>Returns the body of the first declaration block for <paramref name="selector"/>.</summary>
    private static string Block(string css, string selector)
    {
        var start = css.IndexOf(selector, StringComparison.Ordinal);
        Assert.True(start >= 0, $"app.css must contain a '{selector}' block");
        var open = css.IndexOf('{', start);
        var close = css.IndexOf('}', open);
        return css[(open + 1)..close];
    }

    /// <summary>Property names declared in a block body, sorted, so two blocks compare by set. Custom
    /// properties plus color-scheme: the latter is a standard property, not a token, so the original
    /// "--" pattern skipped it entirely and nothing compared it between the two dark blocks — dropping
    /// it from either one would have left every native control and scrollbar light on a dark page with
    /// this test still green.</summary>
    private static List<string> Properties(string block) =>
        Regex.Matches(block, @"(--[a-z0-9-]+|color-scheme)\s*:")
            .Select(m => m.Groups[1].Value)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
}
