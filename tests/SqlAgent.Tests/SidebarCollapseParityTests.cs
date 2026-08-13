using System.Text.RegularExpressions;

namespace SqlAgent.Tests;

/// <summary>
/// The collapsed sidebar is described twice, on purpose, and Phase A left the two copies agreeing only
/// by hand.
///
/// app.css keys on html.sidebar-collapsed, which theme.js sets before first paint — that is the only
/// collapsed styling in effect until the circuit connects, or forever if it never does.
/// Sidebar.razor.css keys on the scoped .collapsed class the component adds after reading the browser
/// back. Both are needed; neither can replace the other. What was missing is anything that fails when
/// they drift, and drift here shows up as a sidebar that renders one width and then snaps to another —
/// the exact defect the pre-paint rule was added to fix.
///
/// bUnit runs no CSS engine, so this compares the two rule sets as source text, the same way
/// DesignSystemTests pins the two dark palettes to the same property set.
/// </summary>
public class SidebarCollapseParityTests
{
    [Fact]
    public void The_pre_paint_and_circuit_collapsed_rules_style_the_same_targets_the_same_way()
    {
        var prePaintCss = File.ReadAllText(RepoPaths.Find("src/SqlAgent.Host/wwwroot/css/app.css"));
        var circuitCss = File.ReadAllText(RepoPaths.Find("src/SqlAgent.Host/Components/Layout/Sidebar.razor.css"));

        // RulesIn below extracts "selector { declarations }" pairs by brace matching alone -- its regex
        // cannot match an @media wrapper, so it necessarily flattens every rule out of whatever media
        // query contains it. That means two rules with identical selector and declaration text compare
        // equal even if one sheet applies them at 1024px and the other at 768px, which is exactly the
        // "renders one width and then snaps to another" defect this file exists to catch. The two
        // sheets' shared breakpoint is therefore compared directly, not inferred from rule content.
        Assert.Equal(WideBlock(circuitCss).Breakpoint, WideBlock(prePaintCss).Breakpoint);

        var prePaint = PrePaintRules(prePaintCss);
        var circuit = CircuitRules(circuitCss);

        Assert.NotEmpty(prePaint);
        Assert.Equal(circuit.Keys.OrderBy(k => k, StringComparer.Ordinal),
                     prePaint.Keys.OrderBy(k => k, StringComparer.Ordinal));
        foreach (var (target, declarations) in circuit)
            Assert.Equal(declarations, prePaint[target]);
    }

    [Fact]
    public void The_pre_paint_rules_cannot_reach_outside_the_sidebar()
    {
        // "html.sidebar-collapsed .nav-label" hides every .nav-label on the page, not just the ones in
        // the sidebar. Nothing else uses that class today, which is exactly why an unscoped selector
        // would sit here undetected until something did. Each selector LIST is split member-by-member
        // (the same way Normalize splits them below) before the check runs: a four-selector rule with
        // one anchored member and three bare ones would otherwise satisfy Assert.Contains on the joined
        // text and let the other three through unanchored.
        var css = File.ReadAllText(RepoPaths.Find("src/SqlAgent.Host/wwwroot/css/app.css"));

        foreach (var selectorList in Selectors(css).Where(s => s.Contains("html.sidebar-collapsed")))
            foreach (var selector in selectorList.Split(','))
                Assert.Contains(".app aside.sidebar", selector.Trim());
    }

    /// <summary>Target-to-declarations for the wide-viewport collapsed rules in app.css, with the
    /// "html.sidebar-collapsed .app aside.sidebar" prefix stripped so the two sheets compare. Scanning
    /// only the wide-viewport block (not the whole file) is what lets the breakpoint check above matter:
    /// otherwise a rule moved to a different @media query, or hoisted out of one entirely, would still
    /// be found by marker text alone.</summary>
    private static Dictionary<string, string> PrePaintRules(string css)
    {
        var (_, wide) = WideBlock(css);
        return Normalize(RulesIn(wide, "html.sidebar-collapsed"),
            ["html.sidebar-collapsed .app aside.sidebar", "html.sidebar-collapsed"]);
    }

    /// <summary>The same, for the rules that apply once the circuit has added the scoped class: the
    /// unconditional ".sidebar.collapsed" rule plus everything inside the wide-viewport media query. The
    /// narrow block is deliberately excluded — it undoes the collapse for the drawer, and the pre-paint
    /// sheet has no counterpart because it is guarded to wide viewports too.</summary>
    private static Dictionary<string, string> CircuitRules(string css)
    {
        var narrow = Block(css, "@media (max-width: 1023px)");
        var (_, wide) = WideBlock(css);
        var unconditional = css.Replace(narrow, "").Replace(wide, "");
        return Normalize(
            RulesIn(unconditional, ".sidebar.collapsed").Concat(RulesIn(wide, ".sidebar.collapsed")).ToList(),
            [".sidebar.collapsed", "::deep"]);
    }

    /// <summary>Finds the sheet's single "@media (min-width: ...)" rule and returns both the breakpoint
    /// number (for the cross-sheet check above) and its block content (so RulesIn only ever sees rules
    /// that actually live at that breakpoint).</summary>
    private static (string Breakpoint, string Block) WideBlock(string css)
    {
        var match = Regex.Match(css, @"@media \(min-width:\s*(\d+)px\)");
        Assert.True(match.Success, "Expected an @media (min-width: ...) rule.");
        return (match.Groups[1].Value, Block(css, match.Value));
    }

    /// <summary>Every "selector { declarations }" pair whose selector mentions <paramref name="marker"/>.</summary>
    private static List<(string Selector, string Declarations)> RulesIn(string css, string marker) =>
        Regex.Matches(StripComments(css), @"([^{}]+)\{([^{}]*)\}")
            .Select(m => (Selector: m.Groups[1].Value.Trim(), Declarations: m.Groups[2].Value))
            .Where(r => r.Selector.Contains(marker, StringComparison.Ordinal))
            .ToList();

    /// <summary>Reduces each rule to (what it targets, what it sets). Selector lists are split, the
    /// sheet-specific prefixes are removed, and declarations are sorted so ordering is not a difference.
    /// A target appearing in more than one rule accumulates: the scoped sheet sets width and flex-basis
    /// unconditionally and overflow inside the media query, while app.css sets all three at once.</summary>
    private static Dictionary<string, string> Normalize(
        List<(string Selector, string Declarations)> rules, string[] prefixes)
    {
        var byTarget = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (selector, declarations) in rules)
        {
            foreach (var one in selector.Split(','))
            {
                var target = one.Trim();
                foreach (var prefix in prefixes) target = target.Replace(prefix, "");
                target = target.Trim();
                if (target.Length == 0) target = ".sidebar";

                if (!byTarget.TryGetValue(target, out var list)) byTarget[target] = list = [];
                list.AddRange(declarations
                    .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            }
        }
        return byTarget.ToDictionary(
            kv => kv.Key,
            kv => string.Join("; ", kv.Value.OrderBy(d => d, StringComparer.Ordinal)),
            StringComparer.Ordinal);
    }

    private static IEnumerable<string> Selectors(string css) =>
        Regex.Matches(StripComments(css), @"([^{}]+)\{[^{}]*\}").Select(m => m.Groups[1].Value.Trim());

    // Comments in both sheets discuss these selectors at length; matching on prose would compare
    // documentation instead of rules.
    private static string StripComments(string css) =>
        Regex.Replace(css, @"/\*.*?\*/", "", RegexOptions.Singleline);

    private static string Block(string css, string selector)
    {
        var start = css.IndexOf(selector, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Expected to find '{selector}'.");
        var open = css.IndexOf('{', start);
        var depth = 0;
        var i = open;
        for (; i < css.Length; i++)
        {
            if (css[i] == '{') depth++;
            else if (css[i] == '}' && --depth == 0) break;
        }
        return css[start..(i + 1)];
    }
}
