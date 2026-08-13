using System.Text.RegularExpressions;

namespace SqlAgent.Tests;

/// <summary>
/// Phase A restyles the existing screens and must not change their behavior. The component tests for
/// those screens (ResultGridTests, SchemaRailTests, WorkspaceTests, ConnectionsPageTests) are the real
/// guard and must stay green. These tests pin the two things those cannot see: that every class name
/// the existing markup already uses actually has a rule somewhere, and that no restyle silently
/// dropped a stylesheet.
/// </summary>
public class RestyleRegressionTests
{
    // "tabs", "transcript" and "question" left with Phase B1: the tab strip is gone and the transcript
    // moved to Components/Shared/Chat, which brings its own stylesheets and its own assertion.
    private static readonly string[] ClassesUsedByExistingMarkup =
    [
        "rail", "tree", "label", "meta", "actions", "grid-scroll",
        "generated-sql", "clarification", "editor",
        "outcome", "outcome-code",
    ];

    // Workspace.razor.css is deliberately absent here: Phase B1 emptied it along with the tab strip it
    // used to style, and this theory exists to catch a stylesheet silently dropped, not one that
    // legitimately has nothing left to say. It stays in the sheets array below, harmlessly, since /sql
    // still needs a page to render even with no scoped rules of its own.
    [Theory]
    [InlineData("src/SqlAgent.Host/Components/Pages/Connections.razor.css")]
    [InlineData("src/SqlAgent.Host/Components/Layout/SchemaRail.razor.css")]
    [InlineData("src/SqlAgent.Host/Components/Shared/ChatOutcome.razor.css")]
    [InlineData("src/SqlAgent.Host/Components/Shared/OutcomeMessage.razor.css")]
    [InlineData("src/SqlAgent.Host/Components/Shared/SqlEditor.razor.css")]
    public void Every_restyled_component_has_a_stylesheet(string path)
    {
        var css = File.ReadAllText(RepoPaths.Find(path));
        Assert.NotEmpty(css.Trim());
    }

    [Fact]
    public void Every_class_the_markup_uses_is_styled_where_it_can_reach_it()
    {
        // The old version concatenated every stylesheet and searched the result, so a rule that had
        // drifted into the wrong component's sheet still passed — precisely the failure that hit Phase A
        // twice. Blazor's scoped CSS is per component: a rule in Foo.razor.css compiles to
        // scopedcss/Components/.../Foo.razor.rz.scp.css and matches only elements Foo itself rendered.
        // So the honest question is not "is this class styled somewhere" but "is it styled somewhere
        // that can reach the markup using it" — this component's own scoped sheet, or a global rule.
        var componentsRoot = Path.GetDirectoryName(RepoPaths.Find("src/SqlAgent.Host/Components/App.razor"))!;
        var global = StripComments(File.ReadAllText(RepoPaths.Find("src/SqlAgent.Host/wwwroot/css/app.css")));

        var unreachable = new List<string>();
        foreach (var razor in Directory.EnumerateFiles(componentsRoot, "*.razor", SearchOption.AllDirectories))
        {
            var markup = File.ReadAllText(razor);
            var scoped = CompiledScopedCssFor(razor);
            foreach (var className in ClassesUsedByExistingMarkup.Where(c => UsesClass(markup, c)))
                if (!IsClassStyled(global, className) && !IsClassStyled(scoped, className))
                    unreachable.Add($"{Path.GetFileName(razor)} uses .{className}");
        }

        Assert.Empty(unreachable);
    }

    /// <summary>The build output for one component's scoped stylesheet, or empty when the component has
    /// none. Missing output for the whole project is a failure rather than an empty pass: a guard that
    /// disappears when its artifact is absent is the same defect in a new place.</summary>
    private static string CompiledScopedCssFor(string razorPath)
    {
        var hostRoot = Path.GetDirectoryName(RepoPaths.Find("src/SqlAgent.Host/Components/App.razor"))!;
        hostRoot = Path.GetDirectoryName(hostRoot)!;
        var scopedRoots = Directory.Exists(Path.Combine(hostRoot, "obj"))
            ? Directory.GetDirectories(Path.Combine(hostRoot, "obj"), "scopedcss", SearchOption.AllDirectories)
            : [];
        Assert.NotEmpty(scopedRoots.Where(r => Directory.Exists(Path.Combine(r, "Components"))));

        var relative = Path.GetRelativePath(hostRoot, razorPath) + ".rz.scp.css";
        foreach (var root in scopedRoots)
        {
            var candidate = Path.Combine(root, relative);
            if (File.Exists(candidate)) return StripComments(File.ReadAllText(candidate));
        }
        return "";
    }

    /// <summary>Whether this component's own markup carries the class, so the question is asked only of
    /// the components that actually use it.
    ///
    /// The brief's original version of this check matched `\b{className}\b` inside the attribute string.
    /// `\b` treats a hyphen as a boundary, so `\blabel\b` matches the tail of "projects-label",
    /// "nav-label", "menu-item-label" and "search-hit-label" — four unrelated, real class names that
    /// happen to end in "-label". Phase B1/B2 introduced all four; the false positives they produce here
    /// are exactly the shape of bug this rewrite exists to avoid, just on the markup side instead of the
    /// CSS side that IsClassStyled below already guards with its own lookahead. HTML's class attribute is
    /// a whitespace-separated token list, so membership has to be checked as a whole token, not a
    /// substring bounded by "not a letter/digit/underscore" — splitting the captured attribute value on
    /// whitespace and comparing tokens exactly is what "carries the class" actually means.</summary>
    private static bool UsesClass(string markup, string className) =>
        Regex.Matches(markup, @"class\s*=\s*""([^""]*)""")
            .Select(m => m.Groups[1].Value)
            .Any(value => value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .Contains(className, StringComparer.Ordinal));

    private static string StripComments(string css) =>
        Regex.Replace(css, @"/\*.*?\*/", "", RegexOptions.Singleline);

    /// <summary>
    /// A bare substring search for ".outcome" is satisfied by ".outcome-code" — a prefix match, not a
    /// real one, that would let every ".outcome { ... }" rule be deleted while ".outcome-code" alone
    /// kept the test green. Requiring the match be followed by a character that cannot continue a CSS
    /// identifier (letters, digits, hyphens, and underscores all can; "{", ",", ":", ".", whitespace,
    /// and "[" cannot) tells a rule for ".outcome" apart from a rule for ".outcome-code".
    /// </summary>
    private static bool IsClassStyled(string css, string className) =>
        Regex.IsMatch(css, $@"\.{Regex.Escape(className)}(?=[{{,:.\[]|\s|$)");

    [Fact]
    public void No_component_stylesheet_hard_codes_a_hex_color()
    {
        // Tokens are the only way both themes stay consistent. A literal hex in a component sheet is a
        // color that will be wrong in one of the two themes.
        var componentSheets = Directory
            .EnumerateFiles(
                Path.GetDirectoryName(RepoPaths.Find("src/SqlAgent.Host/Components/App.razor"))!,
                "*.razor.css",
                SearchOption.AllDirectories)
            .ToList();

        Assert.NotEmpty(componentSheets);
        var offenders = componentSheets
            .Where(f => System.Text.RegularExpressions.Regex.IsMatch(
                File.ReadAllText(f), @"#[0-9a-fA-F]{3,8}\b"))
            .Select(Path.GetFileName)
            .ToList();

        Assert.Empty(offenders);
    }
}
