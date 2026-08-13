using Microsoft.EntityFrameworkCore;

namespace SqlAgent.Storage;

/// <summary>What a hit points at. The modal groups its results by this.</summary>
public enum SearchHitKind { Chat, Message, Project, Database }

/// <summary>
/// One result. <see cref="TargetId"/> is what opening it navigates to — for a message hit that is the
/// chat's id, not the message's, because the chat is what opens. <see cref="Snippet"/> is set only for
/// message hits, where the label alone would not explain why the chat matched.
/// </summary>
public record SearchHit(SearchHitKind Kind, Guid TargetId, string Label, string? Snippet);

/// <summary>
/// One query box over chats, their messages, projects and database connections.
///
/// Deliberately LIKE and not an FTS table: the corpus is one person's history on a local SQLite file,
/// and an FTS index would be a second copy of every message to keep in step for a search that already
/// returns in milliseconds. The cost of that choice is that there is no relevance to rank by, so hits
/// come back newest-first within their kind, which is honest about what the query knows.
/// </summary>
public class SearchService(SqlAgentDbContext db)
{
    /// <summary>Per kind, so one very common word cannot bury the other kinds.</summary>
    private const int PerKindCap = 50;

    /// <summary>Characters either side of the match in a message snippet.</summary>
    private const int SnippetContext = 40;

    /// <summary>The LIKE escape character, passed explicitly to EF's three-argument overload. SQLite has
    /// no default escape character at all — without this, an escaped pattern would match the backslashes
    /// literally.</summary>
    private const string Escape = "\\";

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(string term, CancellationToken ct = default)
    {
        // A blank box is not a query for everything. Returning early also keeps the modal from issuing a
        // full scan on the keystroke that clears the field.
        if (string.IsNullOrWhiteSpace(term)) return [];

        var trimmed = term.Trim();
        var pattern = $"%{EscapeForLike(trimmed)}%";
        var hits = new List<SearchHit>();

        hits.AddRange(await db.Chats
            .Where(c => EF.Functions.Like(c.Title, pattern, Escape))
            .OrderByDescending(c => c.LastMessageAt).ThenByDescending(c => c.CreatedAt)
            .Take(PerKindCap)
            .Select(c => new SearchHit(SearchHitKind.Chat, c.Id, c.Title, null))
            .ToListAsync(ct));

        // One row per chat, not per matching message: the correlated subquery picks the earliest match
        // in each conversation, so a chat that says the word fifteen times still takes one slot.
        var byText = await db.Chats
            .Where(c => c.Messages.Any(m => EF.Functions.Like(m.Text, pattern, Escape)))
            .OrderByDescending(c => c.LastMessageAt).ThenByDescending(c => c.CreatedAt)
            .Take(PerKindCap)
            .Select(c => new
            {
                c.Id,
                c.Title,
                Match = c.Messages
                    .Where(m => EF.Functions.Like(m.Text, pattern, Escape))
                    .OrderBy(m => m.Sequence)
                    .Select(m => m.Text)
                    .FirstOrDefault(),
            })
            .ToListAsync(ct);

        hits.AddRange(byText.Select(c =>
            new SearchHit(SearchHitKind.Message, c.Id, c.Title, Snippet(c.Match ?? "", trimmed))));

        hits.AddRange(await db.Projects
            .Where(p => EF.Functions.Like(p.Name, pattern, Escape))
            .OrderBy(p => p.Name)
            .Take(PerKindCap)
            .Select(p => new SearchHit(SearchHitKind.Project, p.Id, p.Name, null))
            .ToListAsync(ct));

        hits.AddRange(await db.DatabaseConnections
            .Where(d => EF.Functions.Like(d.Name, pattern, Escape))
            .OrderBy(d => d.Name)
            .Take(PerKindCap)
            .Select(d => new SearchHit(SearchHitKind.Database, d.Id, d.Name, null))
            .ToListAsync(ct));

        return hits;
    }

    /// <summary>
    /// Makes a user's text safe to drop inside a LIKE pattern. The escape character goes first: doing it
    /// last would escape the backslashes this method just added. Without this, "50%" matches every row
    /// and "a_b" matches "axb" — quietly, and with nothing for the user to diagnose.
    /// </summary>
    private static string EscapeForLike(string term) => term
        .Replace("\\", "\\\\")
        .Replace("%", "\\%")
        .Replace("_", "\\_");

    /// <summary>
    /// A window around the first occurrence, with ellipses where text was cut. Done here rather than in
    /// SQL because SQLite has no expression for it, and the row is already in memory by this point.
    /// The comparison is case-insensitive to match SQLite's own LIKE; if it still cannot be found — a
    /// non-ASCII case fold the two disagree about — the opening of the message is a better answer than
    /// nothing.
    /// </summary>
    private static string Snippet(string text, string term)
    {
        var at = text.IndexOf(term, StringComparison.OrdinalIgnoreCase);
        if (at < 0) return text.Length <= SnippetContext * 2 ? text : text[..(SnippetContext * 2)] + "…";

        var start = Math.Max(0, at - SnippetContext);
        var end = Math.Min(text.Length, at + term.Length + SnippetContext);
        return (start > 0 ? "…" : "") + text[start..end] + (end < text.Length ? "…" : "");
    }
}
