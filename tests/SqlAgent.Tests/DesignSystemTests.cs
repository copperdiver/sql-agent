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

        var explicitDark = Properties(Block(css, ":root.dark"));
        var systemDark = Properties(Block(css, ":root:not(.light):not(.dark)"));

        Assert.NotEmpty(explicitDark);
        Assert.Equal(explicitDark, systemDark);
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

    /// <summary>Custom-property names declared in a block body, sorted, so two blocks compare by set.</summary>
    private static List<string> Properties(string block) =>
        Regex.Matches(block, @"(--[a-z0-9-]+)\s*:")
            .Select(m => m.Groups[1].Value)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
}
