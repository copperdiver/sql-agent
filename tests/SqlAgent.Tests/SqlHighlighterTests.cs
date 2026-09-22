using SqlAgent.Core;
using SqlAgent.Host.Web;

namespace SqlAgent.Tests;

public class SqlHighlighterTests
{
    [Fact]
    public void Highlighting_escapes_html_and_marks_sql_tokens()
    {
        var markup = SqlHighlighter.Highlight(
            "SELECT '<script>' FROM orders -- comment", DatabaseProviderType.Postgres);

        Assert.Contains("sql-keyword", markup);
        Assert.Contains("&lt;script&gt;", markup);
        Assert.Contains("sql-comment", markup);
        Assert.DoesNotContain("<script>", markup);
    }

    [Fact]
    public void Highlighting_preserves_strings_comments_and_numbers_as_text()
    {
        var markup = SqlHighlighter.Highlight(
            "INSERT INTO orders(id, note) VALUES (42, 'O''Reilly')", DatabaseProviderType.Postgres);

        Assert.Contains("42", markup);
        Assert.Contains("O", markup);
        Assert.Contains("Reilly", markup);
        Assert.Contains("sql-string", markup);
        Assert.Contains("sql-number", markup);
        Assert.Contains("INSERT", markup);
    }
}
