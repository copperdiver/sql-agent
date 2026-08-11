using SqlAgent.Host.Web;

namespace SqlAgent.Tests;

public class ResultExportTests
{
    [Fact]
    public void Csv_writes_a_header_row_and_one_line_per_row()
    {
        var csv = ResultExport.ToCsv(["id", "name"], [new object?[] { 1, "a" }, new object?[] { 2, "b" }]);

        Assert.Equal("id,name\r\n1,a\r\n2,b\r\n", csv);
    }

    [Fact]
    public void Csv_quotes_values_containing_a_comma_quote_or_newline()
    {
        var csv = ResultExport.ToCsv(["v"], [
            new object?[] { "a,b" },
            new object?[] { "say \"hi\"" },
            new object?[] { "line1\nline2" },
        ]);

        Assert.Contains("\"a,b\"", csv);
        Assert.Contains("\"say \"\"hi\"\"\"", csv);   // a quote is escaped by doubling it
        Assert.Contains("\"line1\nline2\"", csv);
    }

    [Fact]
    public void Csv_writes_null_as_an_empty_field_not_the_text_NULL()
    {
        // The grid shows NULL for readability; a CSV consumer expects an empty field.
        var csv = ResultExport.ToCsv(["a", "b"], [new object?[] { null, "" }]);

        Assert.Equal("a,b\r\n,\r\n", csv);
    }

    [Fact]
    public void Json_writes_an_array_of_objects_keyed_by_column()
    {
        var json = ResultExport.ToJson(["id", "name"], [new object?[] { 1, "a" }]);

        Assert.Equal("""[{"id":1,"name":"a"}]""", json);
    }

    [Fact]
    public void Json_preserves_null_as_null()
    {
        var json = ResultExport.ToJson(["id"], [new object?[] { null }]);

        Assert.Equal("""[{"id":null}]""", json);
    }

    [Fact]
    public void Duplicate_column_names_are_disambiguated_so_no_value_is_lost()
    {
        // A projection may legitimately produce two columns of the same name; a JSON object cannot
        // hold two identical keys, so the second occurrence is suffixed rather than silently dropped.
        var json = ResultExport.ToJson(["id", "id"], [new object?[] { 1, 2 }]);

        Assert.Equal("""[{"id":1,"id (2)":2}]""", json);
    }
}
