using SqlAgent.Core;

namespace SqlAgent.Tests;

/// <summary>
/// CD-68 extracted MaxLength/Precision/Scale from the catalogs; <see cref="SchemaColumn.TypeText"/> is the
/// single place that turns those raw facets into the declared type a reader (or an LLM) recognizes, so every
/// surface renders <c>varchar(20)</c> the same way.
/// </summary>
public class SchemaColumnTypeTextTests
{
    [Fact]
    public void TypeText_appends_character_length()
    {
        var column = new SchemaColumn("Name", "nvarchar", true, MaxLength: 100);

        Assert.Equal("nvarchar(100)", column.TypeText);
    }

    [Fact]
    public void TypeText_renders_max_for_an_unbounded_length()
    {
        // SQL Server reports CHARACTER_MAXIMUM_LENGTH = -1 for varchar(max)/nvarchar(max)/varbinary(max).
        var column = new SchemaColumn("Body", "varchar", true, MaxLength: -1);

        Assert.Equal("varchar(max)", column.TypeText);
    }

    [Theory]
    [InlineData("decimal")]
    [InlineData("numeric")]
    [InlineData("DECIMAL")]
    public void TypeText_appends_precision_and_scale_for_exact_numerics(string dataType)
    {
        var column = new SchemaColumn("Total", dataType, false, Precision: 10, Scale: 2);

        Assert.Equal($"{dataType}(10,2)", column.TypeText);
    }

    [Fact]
    public void TypeText_ignores_precision_on_types_that_do_not_declare_it()
    {
        // Both catalogs report precision 10 / scale 0 for int; rendering "int(10,0)" would be wrong SQL and
        // wasted prompt tokens, so only exact-numeric types carry the facets through.
        var column = new SchemaColumn("Id", "int", false, Precision: 10, Scale: 0);

        Assert.Equal("int", column.TypeText);
    }

    [Fact]
    public void TypeText_returns_the_bare_type_when_no_facets_apply()
    {
        var column = new SchemaColumn("PlacedAt", "datetime", false);

        Assert.Equal("datetime", column.TypeText);
    }

    [Fact]
    public void TypeText_prefers_length_when_a_type_reports_both_facets()
    {
        // Postgres reports numeric_precision for some character-ish domains; length is the declared facet.
        var column = new SchemaColumn("Code", "character varying", false, MaxLength: 8, Precision: 32, Scale: 0);

        Assert.Equal("character varying(8)", column.TypeText);
    }
}
