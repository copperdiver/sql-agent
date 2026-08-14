using System.Text.Json.Serialization;

namespace SqlAgent.Core;

/// <summary>
/// Provider-neutral description of a database's structure (CD-50 T4). <see cref="Views"/> is a defaulted
/// trailing parameter so no existing call site breaks and JSON written by a build that predated views
/// deserializes with none — read it through <see cref="ViewList"/> rather than guarding for null at every
/// use.
/// </summary>
public record DatabaseSchema(
    IReadOnlyList<SchemaTable> Tables,
    IReadOnlyList<SchemaView>? Views = null)
{
    [JsonIgnore]
    public IReadOnlyList<SchemaView> ViewList => Views ?? [];
}

/// <summary>
/// One view, with its columns. No primary key, foreign keys, or indexes: a view has none, and the column
/// list is what query generation actually needs. The view's defining SQL is deliberately absent — it is
/// unbounded text headed for a prompt, and including it needs a budget story this phase does not have.
/// </summary>
public record SchemaView(string Schema, string Name, IReadOnlyList<SchemaColumn> Columns);

/// <summary>One base table, with its columns, primary-key column order, outgoing foreign keys, and indexes.</summary>
public record SchemaTable(
    string Schema,
    string Name,
    IReadOnlyList<SchemaColumn> Columns,
    IReadOnlyList<string> PrimaryKey,
    IReadOnlyList<ForeignKey> ForeignKeys,
    IReadOnlyList<SchemaIndex> Indexes);

/// <summary>
/// One column. <paramref name="MaxLength"/> (character/binary length; -1 means MAX),
/// <paramref name="Precision"/>, and <paramref name="Scale"/> are the raw catalog sizing values,
/// null when the type carries no such facet (e.g. <c>int</c> has no length, <c>varchar</c> no scale).
/// </summary>
public record SchemaColumn(
    string Name, string DataType, bool IsNullable,
    int? MaxLength = null, int? Precision = null, int? Scale = null)
{
    /// <summary>
    /// The declared type as a reader would write it — <c>varchar(20)</c>, <c>varchar(max)</c>,
    /// <c>decimal(10,2)</c> — so every surface renders sizing identically. Length wins when a type reports
    /// both facets. Precision and scale are appended only for exact numerics: both catalogs report
    /// precision 10 / scale 0 for <c>int</c>, and "int(10,0)" is neither valid SQL nor useful in a prompt.
    /// [JsonIgnore] keeps it out of the cached schema JSON — it is derived, not stored.
    /// </summary>
    [JsonIgnore]
    public string TypeText => MaxLength switch
    {
        -1 => $"{DataType}(max)",
        int length => $"{DataType}({length})",
        null when IsExactNumeric && Precision is int p => $"{DataType}({p},{Scale ?? 0})",
        _ => DataType,
    };

    private bool IsExactNumeric =>
        DataType.Equals("decimal", StringComparison.OrdinalIgnoreCase) ||
        DataType.Equals("numeric", StringComparison.OrdinalIgnoreCase);
}

/// <summary>A column pointing at another table — the "basic relationship" the model carries.</summary>
public record ForeignKey(string Column, string ReferencedSchema, string ReferencedTable, string ReferencedColumn);

/// <summary>A non-PK index over one or more of the table's own columns (CD-51 Story 1.5). The primary key is
/// already carried by <see cref="SchemaTable.PrimaryKey"/>, so providers omit the PK index here.</summary>
public record SchemaIndex(string Name, IReadOnlyList<string> Columns, bool IsUnique);

/// <summary>
/// Assembles and filters the common <see cref="DatabaseSchema"/>. The provider drivers run their
/// dialect SQL and feed the flat rows here; the assembly/filter logic is dialect-free and unit-tested.
/// </summary>
public static class SchemaModel
{
    /// <summary>
    /// Builds a schema from flat catalog rows. Column and primary-key order is preserved as supplied,
    /// so callers must ORDER BY ordinal position. Rows for unknown tables are grouped on (schema, name).
    /// </summary>
    public static DatabaseSchema Build(
        IEnumerable<(string Schema, string Table, string Column, string DataType, bool Nullable,
            int? MaxLength, int? Precision, int? Scale)> columns,
        IEnumerable<(string Schema, string Table, string Column)> primaryKeys,
        IEnumerable<(string Schema, string Table, string Column, string RefSchema, string RefTable, string RefColumn)> foreignKeys,
        // Index rows are (schema, table, index, column, ordinal, unique). Providers that don't expose index
        // metadata pass nothing — the table simply carries no indexes. Rows must be ORDER BY index, ordinal.
        IEnumerable<(string Schema, string Table, string Index, string Column, bool Unique)>? indexes = null,
        // View column rows, same shape as the table column rows above and ordered the same way. Trailing
        // and defaulted so a provider that does not extract views passes nothing.
        IEnumerable<(string Schema, string View, string Column, string DataType, bool Nullable,
            int? MaxLength, int? Precision, int? Scale)>? views = null)
    {
        var pkByTable = primaryKeys
            .GroupBy(p => (p.Schema, p.Table))
            .ToDictionary(g => g.Key, g => g.Select(p => p.Column).ToList());

        var fkByTable = foreignKeys
            .GroupBy(f => (f.Schema, f.Table))
            .ToDictionary(
                g => g.Key,
                g => g.Select(f => new ForeignKey(f.Column, f.RefSchema, f.RefTable, f.RefColumn)).ToList());

        var ixByTable = (indexes ?? [])
            .GroupBy(i => (i.Schema, i.Table))
            .ToDictionary(
                g => g.Key,
                g => g.GroupBy(i => i.Index)
                      .Select(ix => new SchemaIndex(ix.Key, ix.Select(c => c.Column).ToList(), ix.First().Unique))
                      .ToList());

        var tables = columns
            .GroupBy(c => (c.Schema, c.Table))
            .Select(g => new SchemaTable(
                g.Key.Schema,
                g.Key.Table,
                g.Select(c => new SchemaColumn(c.Column, c.DataType, c.Nullable, c.MaxLength, c.Precision, c.Scale)).ToList(),
                pkByTable.GetValueOrDefault(g.Key, []),
                fkByTable.GetValueOrDefault(g.Key, []),
                ixByTable.GetValueOrDefault(g.Key, [])))
            .ToList();

        // Null rather than an empty list when no rows were supplied: Build's own contract is that a
        // provider which does not extract views produces a schema indistinguishable from one written
        // before views existed, so a cached-JSON round trip cannot tell the two apart.
        var viewList = views is null
            ? null
            : views
                .GroupBy(v => (v.Schema, v.View))
                .Select(g => new SchemaView(
                    g.Key.Schema,
                    g.Key.View,
                    g.Select(c => new SchemaColumn(c.Column, c.DataType, c.Nullable, c.MaxLength, c.Precision, c.Scale)).ToList()))
                .ToList();

        return new DatabaseSchema(tables, viewList);
    }

    /// <summary>
    /// Drops objects the policy says are invisible (CD-50 visibility). Foreign keys that point at a
    /// now-hidden table are also dropped, so a hidden table's name never leaks through a relationship.
    /// Views go through the same predicate as tables — the predicate takes (schema, name) and cannot tell
    /// the two apart, which is the point: one rule, so a view is never the object that quietly stays
    /// visible after its table twin was hidden.
    /// </summary>
    public static DatabaseSchema Filter(DatabaseSchema schema, Func<string, string, bool> isVisible)
    {
        var visible = schema.Tables.Where(t => isVisible(t.Schema, t.Name)).ToList();
        var kept = visible.Select(t => (t.Schema, t.Name)).ToHashSet();

        var filtered = visible
            .Select(t => t with
            {
                ForeignKeys = t.ForeignKeys
                    .Where(fk => kept.Contains((fk.ReferencedSchema, fk.ReferencedTable)))
                    .ToList(),
            })
            .ToList();

        var views = schema.ViewList.Where(v => isVisible(v.Schema, v.Name)).ToList();

        return new DatabaseSchema(filtered, views);
    }
}
