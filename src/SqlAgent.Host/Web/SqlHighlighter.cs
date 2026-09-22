using System.Net;
using System.Text;
using SqlAgent.Core;

namespace SqlAgent.Host.Web;

/// <summary>Small, deliberately non-parsing SQL tokenizer for rendering generated SQL safely.</summary>
public static class SqlHighlighter
{
    private static readonly HashSet<string> Keywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "SELECT", "FROM", "WHERE", "AND", "OR", "NOT", "NULL", "AS", "JOIN", "INNER", "LEFT",
        "RIGHT", "FULL", "OUTER", "ON", "GROUP", "BY", "ORDER", "HAVING", "LIMIT", "OFFSET",
        "TOP", "INSERT", "INTO", "VALUES", "UPDATE", "SET", "DELETE", "RETURNING", "CREATE",
        "ALTER", "DROP", "TABLE", "INDEX", "TRUNCATE", "VIEW", "WITH", "UNION", "ALL", "DISTINCT",
        "CASE", "WHEN", "THEN", "ELSE", "END", "ASC", "DESC", "IS", "IN", "LIKE", "BETWEEN",
        "PRIMARY", "KEY", "REFERENCES", "CONSTRAINT", "IF", "EXISTS", "EXEC", "GRANT", "REVOKE",
    };

    public static string Highlight(string sql, DatabaseProviderType provider)
    {
        _ = provider; // Provider-specific dialect labels live in SqlBlock; token classes are shared.
        var html = new StringBuilder(sql.Length + 64);
        for (var i = 0; i < sql.Length;)
        {
            if (sql[i] == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            {
                var end = sql.IndexOf('\n', i);
                if (end < 0) end = sql.Length;
                Append(html, "sql-comment", sql[i..end]);
                i = end;
                continue;
            }

            if (sql[i] == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                var end = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                end = end < 0 ? sql.Length : end + 2;
                Append(html, "sql-comment", sql[i..end]);
                i = end;
                continue;
            }

            if (sql[i] is '\'' or '"' or '`' || sql[i] == '[')
            {
                var close = sql[i] == '[' ? ']' : sql[i];
                var end = i + 1;
                while (end < sql.Length)
                {
                    if (sql[end] == close)
                    {
                        if (end + 1 < sql.Length && sql[end + 1] == close) { end += 2; continue; }
                        end++;
                        break;
                    }
                    end++;
                }
                Append(html, sql[i] == '\'' ? "sql-string" : "sql-identifier", sql[i..end]);
                i = end;
                continue;
            }

            if (char.IsDigit(sql[i]))
            {
                var end = i + 1;
                while (end < sql.Length && (char.IsDigit(sql[end]) || sql[end] == '.')) end++;
                Append(html, "sql-number", sql[i..end]);
                i = end;
                continue;
            }

            if (char.IsLetter(sql[i]) || sql[i] == '_')
            {
                var end = i + 1;
                while (end < sql.Length && (char.IsLetterOrDigit(sql[end]) || sql[end] == '_')) end++;
                var word = sql[i..end];
                Append(html, Keywords.Contains(word) ? "sql-keyword" : "sql-identifier", word);
                i = end;
                continue;
            }

            html.Append(WebUtility.HtmlEncode(sql[i].ToString()));
            i++;
        }
        return html.ToString();
    }

    private static void Append(StringBuilder html, string className, string text) =>
        html.Append("<span class=\"").Append(className).Append("\">")
            .Append(WebUtility.HtmlEncode(text)).Append("</span>");
}
