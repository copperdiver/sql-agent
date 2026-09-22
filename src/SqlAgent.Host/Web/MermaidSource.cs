using System.Text;
using SqlAgent.Core;

namespace SqlAgent.Host.Web;

/// <summary>Pure Mermaid ER-diagram source generation over an already policy-filtered schema.</summary>
public static class MermaidSource
{
    public static string Build(DatabaseSchema schema)
    {
        var tables = schema.Tables.OrderBy(t => t.Schema).ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var ids = tables.ToDictionary(t => (t.Schema, t.Name), TableId);
        var sb = new StringBuilder("erDiagram\n");

        foreach (var table in tables)
        {
            var foreignColumns = table.ForeignKeys.Select(f => f.Column)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            sb.Append("  ").Append(ids[(table.Schema, table.Name)]).AppendLine(" {");
            foreach (var column in table.Columns)
            {
                var markers = new List<string>();
                if (table.PrimaryKey.Contains(column.Name, StringComparer.OrdinalIgnoreCase)) markers.Add("PK");
                if (foreignColumns.Contains(column.Name)) markers.Add("FK");
                sb.Append("    ").Append(MermaidType(column.TypeText)).Append(' ')
                    .Append(Identifier(column.Name));
                if (markers.Count > 0) sb.Append(' ').Append(string.Join(",", markers));
                sb.AppendLine();
            }
            sb.AppendLine("  }");
        }

        foreach (var table in tables)
        foreach (var foreignKey in table.ForeignKeys.OrderBy(f => f.Column, StringComparer.OrdinalIgnoreCase))
        {
            if (!ids.TryGetValue((foreignKey.ReferencedSchema, foreignKey.ReferencedTable), out var referenced)) continue;
            sb.Append("  ").Append(referenced).Append(" ||--o{ ")
                .Append(ids[(table.Schema, table.Name)]).Append(" : \"")
                .Append(Identifier(foreignKey.Column)).AppendLine("\"");
        }

        return sb.ToString();
    }

    private static string TableId(SchemaTable table) => Identifier($"{table.Schema}_{table.Name}");

    private static string Identifier(string value)
    {
        var chars = value.Select(c => char.IsLetterOrDigit(c) || c == '_' ? c : '_').ToArray();
        var result = new string(chars);
        return string.IsNullOrWhiteSpace(result) ? "unnamed" : result;
    }

    private static string MermaidType(string value) => Identifier(value).ToLowerInvariant();
}
