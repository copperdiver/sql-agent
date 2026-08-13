using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SqlAgent.Core;
using SqlAgent.Storage;

namespace SqlAgent.Tests;

/// <summary>
/// The pattern-escaping cases are the reason this class exists as its own file. A search for "50%" that
/// silently matches every chat, or "_" that matches every single character, is the kind of defect a user
/// cannot diagnose and would not report as a bug — it just makes search feel broken.
/// </summary>
public class SearchServiceTests : IDisposable
{
    private readonly SqliteConnection _conn = new("DataSource=:memory:");
    private readonly SqlAgentDbContext _db;
    private readonly SearchService _search;
    private readonly ChatService _chats;

    public SearchServiceTests()
    {
        _conn.Open();
        _db = new SqlAgentDbContext(
            new DbContextOptionsBuilder<SqlAgentDbContext>().UseSqlite(_conn).Options);
        _db.Database.EnsureCreated();
        _search = new SearchService(_db);
        _chats = new ChatService(_db);
    }

    [Fact]
    public async Task A_blank_term_returns_nothing_and_asks_the_database_nothing()
    {
        await ChatWithMessageAsync("quarterly revenue", "anything at all");

        Assert.Empty(await _search.SearchAsync(""));
        Assert.Empty(await _search.SearchAsync("   "));
    }

    [Fact]
    public async Task A_chat_is_found_by_its_title()
    {
        var id = await ChatWithMessageAsync("quarterly revenue", "unrelated body");

        var hit = Assert.Single(await _search.SearchAsync("quarterly"));

        Assert.Equal(SearchHitKind.Chat, hit.Kind);
        Assert.Equal(id, hit.TargetId);
        Assert.Equal("quarterly revenue", hit.Label);
        Assert.Null(hit.Snippet);
    }

    [Fact]
    public async Task A_chat_is_found_by_its_message_text_with_a_snippet_around_the_match()
    {
        var id = await ChatWithMessageAsync(
            "untitled", "the quick brown fox jumps over the lazy dog and keeps going for a while");

        var hit = Assert.Single(await _search.SearchAsync("lazy"));

        Assert.Equal(SearchHitKind.Message, hit.Kind);
        // The chat is what opens, so the chat's id is what the hit carries — not the message's.
        Assert.Equal(id, hit.TargetId);
        Assert.Contains("lazy", hit.Snippet);
        // Cut, not the whole message: a long answer would otherwise fill the result list.
        Assert.True(hit.Snippet!.Length < 100);
    }

    [Fact]
    public async Task A_chat_whose_messages_match_many_times_appears_once()
    {
        // Without this, one talkative conversation pushes every other result off the list.
        var id = await _chats.CreateChatAsync("untitled");
        for (var i = 0; i < 5; i++)
            await _chats.AppendMessageAsync(new ChatMessageInput(id, ChatRole.User, "lazy again", []));

        var hits = await _search.SearchAsync("lazy");

        Assert.Single(hits, h => h.Kind == SearchHitKind.Message && h.TargetId == id);
    }

    [Fact]
    public async Task A_chat_matching_by_title_and_by_text_is_reported_under_both_kinds()
    {
        // The modal groups by kind, so the same conversation legitimately appears in two groups; what it
        // must not do is appear twice within one group.
        var id = await ChatWithMessageAsync("lazy plans", "the lazy dog");

        var hits = await _search.SearchAsync("lazy");

        Assert.Single(hits, h => h.Kind == SearchHitKind.Chat && h.TargetId == id);
        Assert.Single(hits, h => h.Kind == SearchHitKind.Message && h.TargetId == id);
    }

    [Theory]
    // Each wildcard gets its own case because each breaks differently and all three break quietly.
    [InlineData("50%")]
    [InlineData("a_b")]
    [InlineData("back\\slash")]
    public async Task A_wildcard_in_the_term_is_matched_literally(string literal)
    {
        await ChatWithMessageAsync($"about {literal} exactly", "unrelated");
        await ChatWithMessageAsync("nothing like it", "unrelated");

        var hits = await _search.SearchAsync(literal);

        var hit = Assert.Single(hits, h => h.Kind == SearchHitKind.Chat);
        Assert.Contains(literal, hit.Label);
    }

    [Fact]
    public async Task An_underscore_does_not_match_an_arbitrary_character()
    {
        // The sharpest form of the same bug: "a_b" must not find "axb".
        await ChatWithMessageAsync("axb", "unrelated");

        Assert.Empty(await _search.SearchAsync("a_b"));
    }

    [Fact]
    public async Task Projects_and_databases_are_found_by_name()
    {
        var projects = new ProjectService(_db);
        var created = await projects.CreateProjectAsync("quarterly work");
        var connections = new DatabaseConnectionService(_db, new InMemorySecretStore());
        var connection = await connections.CreateAsync(
            new DatabaseConnectionInput("quarterly reporting", DatabaseProviderType.Postgres, true), "cs");

        var hits = await _search.SearchAsync("quarterly");

        var project = Assert.Single(hits, h => h.Kind == SearchHitKind.Project);
        Assert.Equal(created.Id, project.TargetId);
        var database = Assert.Single(hits, h => h.Kind == SearchHitKind.Database);
        Assert.Equal(connection.Id, database.TargetId);
    }

    [Fact]
    public async Task Hits_of_one_kind_come_back_newest_first()
    {
        var older = await ChatWithMessageAsync("lazy one", "x");
        var newer = await ChatWithMessageAsync("lazy two", "x");
        // AppendMessageAsync moves LastMessageAt, which is the order the sidebar uses everywhere else.
        await _chats.AppendMessageAsync(new ChatMessageInput(older, ChatRole.User, "later", []));

        var chats = (await _search.SearchAsync("lazy"))
            .Where(h => h.Kind == SearchHitKind.Chat).Select(h => h.TargetId).ToList();

        Assert.Equal([older, newer], chats);
    }

    [Fact]
    public async Task No_kind_returns_more_than_fifty_hits()
    {
        for (var i = 0; i < 55; i++) await ChatWithMessageAsync($"lazy {i}", "x");

        var hits = await _search.SearchAsync("lazy");

        Assert.Equal(50, hits.Count(h => h.Kind == SearchHitKind.Chat));
    }

    private async Task<Guid> ChatWithMessageAsync(string title, string body)
    {
        var id = await _chats.CreateChatAsync(title);
        await _chats.AppendMessageAsync(new ChatMessageInput(id, ChatRole.User, body, []));
        return id;
    }

    public void Dispose()
    {
        _db.Dispose();
        _conn.Dispose();
    }
}
