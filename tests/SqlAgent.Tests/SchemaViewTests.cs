using SqlAgent.Core;

namespace SqlAgent.Tests;

/// <summary>
/// Views enter the model the same way tables do — flat catalog rows in, grouped objects out — and leave
/// it the same way: through the one visibility predicate. A view that survives Filter when its table
/// twin would not is a hidden object reaching the prompt.
/// </summary>
public class SchemaViewTests
{
    private static (string, string, string, string, bool, int?, int?, int?) Col(
        string schema, string obj, string column, string type = "int", bool nullable = false)
        => (schema, obj, column, type, nullable, null, null, null);

    [Fact]
    public void A_schema_built_without_view_rows_reports_no_views()
    {
        // Every existing call site passes nothing. ViewList, not Views, is what callers read, so the
        // absent case must be an empty list rather than a null they have to guard.
        var schema = SchemaModel.Build([Col("dbo", "Orders", "Id")], [], []);

        Assert.Empty(schema.ViewList);
        Assert.Null(schema.Views);
    }

    [Fact]
    public void View_rows_group_into_views_with_their_columns_in_order()
    {
        var schema = SchemaModel.Build(
            [Col("dbo", "Orders", "Id")],
            [], [], null,
            [Col("dbo", "OrderSummary", "OrderId"), Col("dbo", "OrderSummary", "Total", "decimal"),
             Col("sales", "TopCustomers", "Name", "varchar")]);

        Assert.Single(schema.Tables);
        Assert.Equal(2, schema.ViewList.Count);

        var summary = schema.ViewList.Single(v => v.Name == "OrderSummary");
        Assert.Equal("dbo", summary.Schema);
        Assert.Equal(["OrderId", "Total"], summary.Columns.Select(c => c.Name));
        Assert.Equal("decimal", summary.Columns[1].DataType);

        Assert.Equal("sales", schema.ViewList.Single(v => v.Name == "TopCustomers").Schema);
    }

    [Fact]
    public void A_table_and_a_view_of_the_same_name_in_different_schemas_stay_separate()
    {
        // The unique index on (connection, schema, name) makes this legal, and the grouping key must be
        // the pair rather than the bare name or the two collapse into one object.
        var schema = SchemaModel.Build(
            [Col("dbo", "Orders", "Id")],
            [], [], null,
            [Col("sales", "Orders", "Id")]);

        Assert.Equal("dbo", Assert.Single(schema.Tables).Schema);
        Assert.Equal("sales", Assert.Single(schema.ViewList).Schema);
    }

    [Fact]
    public void Filter_drops_a_hidden_view_and_keeps_a_visible_one()
    {
        var schema = SchemaModel.Build(
            [Col("dbo", "Orders", "Id")],
            [], [], null,
            [Col("dbo", "Secret", "Id"), Col("dbo", "Public", "Id")]);

        var filtered = SchemaModel.Filter(schema, (s, n) => n != "Secret");

        Assert.Equal("Public", Assert.Single(filtered.ViewList).Name);
        Assert.Single(filtered.Tables);
    }

    [Fact]
    public void Filter_applies_the_same_predicate_to_a_view_as_to_a_table()
    {
        // The predicate takes (schema, name) and cannot tell the two kinds apart. That is the point:
        // one rule, so a view cannot be the thing that quietly stays visible.
        var schema = SchemaModel.Build(
            [Col("dbo", "Orders", "Id")],
            [], [], null,
            [Col("dbo", "Orders2", "Id")]);

        var seen = new List<string>();
        SchemaModel.Filter(schema, (s, n) => { seen.Add($"{s}.{n}"); return true; });

        Assert.Contains("dbo.Orders", seen);
        Assert.Contains("dbo.Orders2", seen);
    }

    [Fact]
    public void Filter_returns_an_empty_view_list_rather_than_null_when_there_were_none()
    {
        var filtered = SchemaModel.Filter(SchemaModel.Build([Col("dbo", "Orders", "Id")], [], []), (_, _) => true);

        Assert.Empty(filtered.ViewList);
    }
}
