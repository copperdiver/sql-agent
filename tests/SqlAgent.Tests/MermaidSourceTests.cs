using SqlAgent.Core;
using SqlAgent.Host.Web;

namespace SqlAgent.Tests;

public class MermaidSourceTests
{
    [Fact]
    public void Builds_deterministic_er_diagram_with_primary_and_foreign_keys()
    {
        var schema = new DatabaseSchema([
            new SchemaTable("public", "orders",
                [new SchemaColumn("id", "integer", false), new SchemaColumn("customer_id", "integer", false)],
                ["id"], [new ForeignKey("customer_id", "public", "customers", "id")], []),
            new SchemaTable("public", "customers",
                [new SchemaColumn("id", "integer", false)], ["id"], [], []),
        ]);

        var source = MermaidSource.Build(schema);

        Assert.StartsWith("erDiagram", source);
        Assert.Contains("public_orders", source);
        Assert.Contains("id PK", source);
        Assert.Contains("customer_id FK", source);
        Assert.Contains("public_customers ||--o{ public_orders", source);
    }

    [Fact]
    public void Uses_only_the_schema_it_receives_so_hidden_objects_cannot_leak_into_the_diagram()
    {
        var source = MermaidSource.Build(new DatabaseSchema([
            new SchemaTable("public", "orders", [new SchemaColumn("id", "int", false)], ["id"], [], []),
        ]));

        Assert.Contains("public_orders", source);
        Assert.DoesNotContain("secrets", source);
    }
}
