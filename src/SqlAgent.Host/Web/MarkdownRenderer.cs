using Markdig;

namespace SqlAgent.Host.Web;

/// <summary>One server-side Markdown policy for assistant prose. Raw HTML is disabled so model text can
/// never turn into an arbitrary element when it is inserted as markup.</summary>
public static class MarkdownRenderer
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .DisableHtml()
        .Build();

    public static string ToHtml(string? markdown) =>
        Markdown.ToHtml(markdown ?? "", Pipeline);
}
