using Bunit;
using SqlAgent.Core;
using SqlAgent.Host.Components.Shared.Chat;

namespace SqlAgent.Tests;

public class ErDiagramTests
{
    [Fact]
    public void Renders_tables_and_diagram_controls_from_the_visible_schema()
    {
        using var ctx = new Bunit.TestContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var schema = new DatabaseSchema(
        [
            new SchemaTable("public", "orders", [new SchemaColumn("id", "integer", false)], ["id"], [], []),
            new SchemaTable("public", "customers", [new SchemaColumn("id", "integer", false)], ["id"], [], []),
        ]);

        var diagram = ctx.RenderComponent<ErDiagram>(p => p
            .Add(x => x.Schema, schema)
            .Add(x => x.ConnectionName, "prod"));

        Assert.Contains("prod", diagram.Markup);
        Assert.Contains("orders", diagram.Markup);
        Assert.Contains("customers", diagram.Markup);
        Assert.NotEmpty(diagram.FindAll("[data-testid=diagram-zoom-in]"));
        Assert.NotEmpty(diagram.FindAll("[data-testid=diagram-download]"));
        Assert.NotEmpty(diagram.FindAll("[data-testid=diagram-fullscreen]"));
        Assert.Contains("erDiagram", diagram.Find("[data-testid=diagram-source]").TextContent);
    }
}
