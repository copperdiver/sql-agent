using System.Text;
using System.Text.Json;

namespace SqlAgent.Host.Web;

/// <summary>
/// Serializes an already-fetched result set. Export never re-queries: the user downloads exactly the
/// rows they were shown, and no second audit entry appears for a button that only formats data.
/// </summary>
public static class ResultExport
{
    public static string ToCsv(IReadOnlyList<string> columns, IReadOnlyList<IReadOnlyList<object?>> rows)
    {
        var sb = new StringBuilder();
        sb.Append(string.Join(',', columns.Select(Escape))).Append("\r\n");
        foreach (var row in rows)
            sb.Append(string.Join(',', row.Select(v => Escape(v?.ToString())))).Append("\r\n");
        return sb.ToString();
    }

    public static string ToJson(IReadOnlyList<string> columns, IReadOnlyList<IReadOnlyList<object?>> rows)
    {
        var names = Disambiguate(columns);
        var objects = rows.Select(row => names
            .Select((name, i) => (name, value: i < row.Count ? row[i] : null))
            .ToDictionary(p => p.name, p => p.value));
        return JsonSerializer.Serialize(objects);
    }

    private static readonly char[] CharsRequiringQuoting = [',', '"', '\n', '\r'];

    /// <summary>A null becomes an empty field; only comma, quote, or newline force quoting.</summary>
    private static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        if (value.AsSpan().IndexOfAny(CharsRequiringQuoting) < 0) return value;
        return $"\"{value.Replace("\"", "\"\"")}\"";
    }

    /// <summary>A JSON object cannot hold duplicate keys, so repeats get a " (n)" suffix.</summary>
    private static List<string> Disambiguate(IReadOnlyList<string> columns)
    {
        var seen = new Dictionary<string, int>();
        var result = new List<string>(columns.Count);
        foreach (var raw in columns)
        {
            var name = string.IsNullOrEmpty(raw) ? "(column)" : raw;
            if (seen.TryGetValue(name, out var n))
            {
                seen[name] = n + 1;
                name = $"{name} ({n + 1})";
            }
            else
            {
                seen[name] = 1;
            }
            result.Add(name);
        }
        return result;
    }
}
