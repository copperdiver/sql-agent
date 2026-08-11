using SqlAgent.Core;

namespace SqlAgent.Tests;

public class SchemaModelBuildTests
{
    [Fact]
    public void Build_groups_columns_pks_and_fks_per_table_preserving_order()
    {
        var schema = SchemaModel.Build(
            columns:
            [
                ("dbo", "Orders", "Id", "int", false, null, 10, 0),
                ("dbo", "Orders", "CustomerId", "int", false, null, 10, 0),
                ("dbo", "Customers", "Id", "int", false, null, 10, 0),
                ("dbo", "Customers", "Name", "nvarchar", true, 100, null, null),
            ],
            primaryKeys:
            [
                ("dbo", "Orders", "Id"),
                ("dbo", "Customers", "Id"),
            ],
            foreignKeys:
            [
                ("dbo", "Orders", "CustomerId", "dbo", "Customers", "Id"),
            ]);

        var orders = schema.Tables.Single(t => t.Name == "Orders");
        Assert.Equal(["Id", "CustomerId"], orders.Columns.Select(c => c.Name));
        Assert.Equal(["Id"], orders.PrimaryKey);
        var fk = Assert.Single(orders.ForeignKeys);
        Assert.Equal(("CustomerId", "dbo", "Customers", "Id"),
            (fk.Column, fk.ReferencedSchema, fk.ReferencedTable, fk.ReferencedColumn));

        var customers = schema.Tables.Single(t => t.Name == "Customers");
        Assert.Empty(customers.ForeignKeys);
        var name = customers.Columns.Single(c => c.Name == "Name");
        Assert.True(name.IsNullable);
        Assert.Equal(100, name.MaxLength);
        Assert.Null(name.Precision);
    }

    [Fact]
    public void Build_maps_a_composite_fk_as_ordered_column_pairs()
    {
        // Each local column pairs with the referenced column at the same position — never a cross product.
        var schema = SchemaModel.Build(
            columns: [("dbo", "OrderLine", "OrderId", "int", false, null, 10, 0), ("dbo", "OrderLine", "Sku", "varchar", false, 20, null, null)],
            primaryKeys: [],
            foreignKeys:
            [
                ("dbo", "OrderLine", "OrderId", "dbo", "OrderItem", "OrderId"),
                ("dbo", "OrderLine", "Sku", "dbo", "OrderItem", "Sku"),
            ]);

        var fks = schema.Tables.Single().ForeignKeys;
        Assert.Equal(2, fks.Count);
        Assert.Equal(
            [("OrderId", "OrderId"), ("Sku", "Sku")],
            fks.Select(f => (f.Column, f.ReferencedColumn)));
    }

    [Fact]
    public void Build_groups_index_columns_in_order_and_keeps_uniqueness()
    {
        var schema = SchemaModel.Build(
            columns: [("dbo", "Orders", "CustomerId", "int", false), ("dbo", "Orders", "PlacedAt", "datetime", false)],
            primaryKeys: [],
            foreignKeys: [],
            indexes:
            [
                ("dbo", "Orders", "IX_Cust_Date", "CustomerId", false),
                ("dbo", "Orders", "IX_Cust_Date", "PlacedAt", false),
                ("dbo", "Orders", "UX_Cust", "CustomerId", true),
            ]);

        var ix = schema.Tables.Single().Indexes;
        Assert.Equal(2, ix.Count);
        var composite = ix.Single(i => i.Name == "IX_Cust_Date");
        Assert.Equal(["CustomerId", "PlacedAt"], composite.Columns); // column order preserved
        Assert.False(composite.IsUnique);
        Assert.True(ix.Single(i => i.Name == "UX_Cust").IsUnique);
    }

    [Fact]
    public void Build_defaults_indexes_to_empty_when_provider_supplies_none()
    {
        var schema = SchemaModel.Build(
            columns: [("dbo", "Orders", "Id", "int", false)],
            primaryKeys: [],
            foreignKeys: []);

        Assert.Empty(schema.Tables.Single().Indexes);
    }

    [Fact]
    public void Build_handles_a_table_with_no_keys()
    {
        var schema = SchemaModel.Build(
            columns: [("public", "logs", "msg", "text", true, null, null, null)],
            primaryKeys: [],
            foreignKeys: []);

        var t = Assert.Single(schema.Tables);
        Assert.Empty(t.PrimaryKey);
        Assert.Empty(t.ForeignKeys);
    }
}

public class SchemaModelFilterTests
{
    private static DatabaseSchema TwoTables() => SchemaModel.Build(
        columns:
        [
            ("dbo", "Public", "Id", "int", false, null, 10, 0),
            ("dbo", "Secret", "Id", "int", false, null, 10, 0),
        ],
        primaryKeys: [],
        foreignKeys:
        [
            ("dbo", "Public", "SecretId", "dbo", "Secret", "Id"),
        ]);

    [Fact]
    public void Filter_omits_invisible_tables()
    {
        var filtered = SchemaModel.Filter(TwoTables(), (_, table) => table != "Secret");

        var t = Assert.Single(filtered.Tables);
        Assert.Equal("Public", t.Name);
    }

    [Fact]
    public void Filter_drops_fks_pointing_at_a_hidden_table_so_its_name_never_leaks()
    {
        var filtered = SchemaModel.Filter(TwoTables(), (_, table) => table != "Secret");

        Assert.Empty(filtered.Tables.Single().ForeignKeys);
    }

    [Fact]
    public void Filter_keeps_everything_when_all_visible()
    {
        var filtered = SchemaModel.Filter(TwoTables(), (_, _) => true);

        Assert.Equal(2, filtered.Tables.Count);
        Assert.Single(filtered.Tables.Single(t => t.Name == "Public").ForeignKeys);
    }

    [Fact]
    public void Filter_keeps_indexes_of_visible_tables_and_drops_those_of_hidden_ones()
    {
        var schema = SchemaModel.Build(
            columns: [("dbo", "Public", "Id", "int", false), ("dbo", "Secret", "Token", "text", false)],
            primaryKeys: [],
            foreignKeys: [],
            indexes:
            [
                ("dbo", "Public", "IX_Public_Id", "Id", false),
                ("dbo", "Secret", "IX_Secret_Token", "Token", true),
            ]);

        var filtered = SchemaModel.Filter(schema, (_, table) => table != "Secret");

        var kept = Assert.Single(filtered.Tables);
        Assert.Equal("Public", kept.Name);
        Assert.Equal("IX_Public_Id", Assert.Single(kept.Indexes).Name); // own index survives; hidden table's index is gone with it
    }
}
