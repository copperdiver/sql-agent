using System.Globalization;
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
            sb.Append(string.Join(',', row.Select(v => Escape(FormatInvariant(v))))).Append("\r\n");
        return sb.ToString();
    }

    /// <summary>
    /// Renders a cell value the same way regardless of the host machine's locale, and preserves binary
    /// data instead of losing it to a bare "System.Byte[]" from the default ToString(). JSON does not
    /// need this: JsonSerializer resolves the runtime type itself (base64-encoding byte[]) and its
    /// number/date formatting is culture-invariant by construction.
    /// </summary>
    private static string? FormatInvariant(object? value) => value switch
    {
        null => null,
        byte[] bytes => Convert.ToBase64String(bytes),
        // "O" is the round-trip format: fixed shape, no locale-dependent separators or ordering.
        DateTime dt => dt.ToString("O", CultureInfo.InvariantCulture),
        DateTimeOffset dto => dto.ToString("O", CultureInfo.InvariantCulture),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString(),
    };

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
