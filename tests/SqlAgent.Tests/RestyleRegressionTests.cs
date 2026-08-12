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
    private static readonly string[] ClassesUsedByExistingMarkup =
    [
        "tabs", "rail", "tree", "label", "meta", "actions", "grid-scroll",
        "transcript", "question", "generated-sql", "clarification", "editor",
        "outcome", "outcome-code",
    ];

    [Theory]
    [InlineData("src/SqlAgent.Host/Components/Pages/Connections.razor.css")]
    [InlineData("src/SqlAgent.Host/Components/Pages/Workspace.razor.css")]
    [InlineData("src/SqlAgent.Host/Components/Layout/SchemaRail.razor.css")]
    [InlineData("src/SqlAgent.Host/Components/Shared/ResultGrid.razor.css")]
    [InlineData("src/SqlAgent.Host/Components/Shared/ChatOutcome.razor.css")]
    [InlineData("src/SqlAgent.Host/Components/Shared/OutcomeMessage.razor.css")]
    [InlineData("src/SqlAgent.Host/Components/Shared/SqlEditor.razor.css")]
    public void Every_restyled_component_has_a_stylesheet(string path)
    {
        var css = File.ReadAllText(RepoPaths.Find(path));
        Assert.NotEmpty(css.Trim());
    }

    [Fact]
    public void Every_class_the_existing_markup_uses_is_styled_somewhere()
    {
        // These class names were in the markup with no stylesheet at all, which is how the UI shipped
        // unstyled. Any one of them left unstyled is an unstyled region of a real screen.
        var sheets = new[]
        {
            "src/SqlAgent.Host/wwwroot/css/app.css",
            "src/SqlAgent.Host/Components/Pages/Connections.razor.css",
            "src/SqlAgent.Host/Components/Pages/Workspace.razor.css",
            "src/SqlAgent.Host/Components/Layout/SchemaRail.razor.css",
            "src/SqlAgent.Host/Components/Shared/ResultGrid.razor.css",
            "src/SqlAgent.Host/Components/Shared/ChatOutcome.razor.css",
            "src/SqlAgent.Host/Components/Shared/OutcomeMessage.razor.css",
            "src/SqlAgent.Host/Components/Shared/SqlEditor.razor.css",
        };
        var all = string.Concat(sheets.Select(s => File.ReadAllText(RepoPaths.Find(s))));

        var unstyled = ClassesUsedByExistingMarkup.Where(c => !all.Contains($".{c}", StringComparison.Ordinal)).ToList();

        Assert.Empty(unstyled);
    }

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
