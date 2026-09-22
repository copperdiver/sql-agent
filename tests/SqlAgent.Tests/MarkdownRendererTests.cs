using SqlAgent.Host.Web;

namespace SqlAgent.Tests;

public class MarkdownRendererTests
{
    [Fact]
    public void Renders_basic_markdown_but_disables_raw_html()
    {
        var html = MarkdownRenderer.ToHtml("**safe** <script>alert(1)</script>");

        Assert.Contains("<strong>safe</strong>", html);
        Assert.DoesNotContain("<script>", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("&lt;script&gt;", html, StringComparison.OrdinalIgnoreCase);
    }
}
