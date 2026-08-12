using System.Globalization;
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

    [Fact]
    public void A_byte_array_cell_exports_as_base64_in_CSV_matching_the_JSON_encoding()
    {
        // varbinary/bytea/rowversion columns are byte[] at the ADO.NET level. Plain ToString() on a
        // byte[] yields the literal text "System.Byte[]" and silently destroys the data; base64
        // preserves it, and matching JsonSerializer's own byte[] encoding keeps the two exports in
        // agreement for the same underlying value.
        var bytes = new byte[] { 0, 1, 2, 250, 255 };
        var expectedBase64 = Convert.ToBase64String(bytes);

        var csv = ResultExport.ToCsv(["data"], [new object?[] { bytes }]);
        var json = ResultExport.ToJson(["data"], [new object?[] { bytes }]);

        Assert.Equal($"data\r\n{expectedBase64}\r\n", csv);
        Assert.Contains($"\"{expectedBase64}\"", json);
    }

    [Fact]
    public void Decimal_and_DateTime_values_export_invariantly_in_CSV_regardless_of_current_culture()
    {
        // On a comma-decimal locale, ToString() renders 1.5 as "1,5" — the CSV then only avoids
        // corruption because the comma happens to trigger quoting. Formatting must be invariant so a
        // CSV's numbers mean the same thing no matter which machine opened the app.
        var original = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
        try
        {
            var csv = ResultExport.ToCsv(
                ["price", "when"],
                [new object?[] { 1.5m, new DateTime(2026, 8, 11, 13, 30, 0, DateTimeKind.Utc) }]);

            Assert.Contains("1.5", csv);
            Assert.DoesNotContain("1,5", csv);
            // A round-trippable invariant format (the "O" specifier), not whatever de-DE would produce.
            Assert.Contains("2026-08-11T13:30:00.0000000", csv);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    // Csv_writes_null_as_an_empty_field_not_the_text_NULL above already pins the null/empty-string
    // collapse. A second copy of it (identical body and assertion, added while fixing the byte[] and
    // culture issues) used to sit here and is removed: it could only ever fail at the same moment.

    [Fact]
    public void A_duplicate_column_does_not_collide_with_a_real_column_already_named_like_the_suffix()
    {
        // A projection can legitimately produce a column literally named "id (2)". Suffixing the
        // second "id" from a per-name counter regenerated exactly that name, and ToDictionary threw
        // ArgumentException straight out of the Export JSON button. Every key must be distinct, and
        // no value may be dropped.
        var json = ResultExport.ToJson(["id (2)", "id", "id"], [new object?[] { 1, 2, 3 }]);

        Assert.Equal("""[{"id (2)":1,"id":2,"id (3)":3}]""", json);
    }
}
