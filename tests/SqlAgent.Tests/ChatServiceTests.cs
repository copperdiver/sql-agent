using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SqlAgent.Storage;

namespace SqlAgent.Tests;

public class ChatServiceTests : IDisposable
{
    private readonly SqliteConnection _conn = new("DataSource=:memory:");
    private readonly SqlAgentDbContext _db;
    private readonly ChatService _chats;

    public ChatServiceTests()
    {
        _conn.Open();
        _db = new SqlAgentDbContext(
            new DbContextOptionsBuilder<SqlAgentDbContext>().UseSqlite(_conn).Options);
        _db.Database.EnsureCreated();
        _chats = new ChatService(_db);
    }

    [Fact]
    public async Task Messages_are_numbered_in_the_order_they_are_appended()
    {
        // Sequence is what orders a reloaded transcript. CreatedAt is not a substitute: two messages
        // written inside the same millisecond would reload in arbitrary order, and a turn is exactly
        // that — the question and its answer are appended back to back.
        var chat = await _chats.CreateChatAsync("t");

        await _chats.AppendMessageAsync(new ChatMessageInput(chat, ChatRole.User, "q", []));
        await _chats.AppendMessageAsync(new ChatMessageInput(chat, ChatRole.Assistant, "a", []));

        var detail = await _chats.GetChatAsync(chat);
        Assert.Equal([0, 1], detail!.Messages.Select(m => m.Sequence));
        Assert.Equal([ChatRole.User, ChatRole.Assistant], detail.Messages.Select(m => m.Role));
    }

    [Fact]
    public async Task Appending_a_message_moves_the_chat_to_the_top_of_history()
    {
        var older = await _chats.CreateChatAsync("older");
        var newer = await _chats.CreateChatAsync("newer");
        await _chats.AppendMessageAsync(new ChatMessageInput(newer, ChatRole.User, "q", []));
        await _chats.AppendMessageAsync(new ChatMessageInput(older, ChatRole.User, "q", []));

        var history = await _chats.ListHistoryAsync();

        Assert.Equal("older", history[0].Title);
    }

    [Fact]
    public async Task A_message_keeps_the_databases_it_was_sent_with()
    {
        // The whole point of the attachment snapshot: a transcript that cannot say which database a
        // question was asked against is not an audit trail.
        var chat = await _chats.CreateChatAsync("t");
        var id = Guid.NewGuid();

        await _chats.AppendMessageAsync(new ChatMessageInput(
            chat, ChatRole.User, "q", [new ChatDatabaseRef(id, "prod"), new ChatDatabaseRef(null, "gone")]));

        var message = (await _chats.GetChatAsync(chat))!.Messages.Single();
        Assert.Equal(["gone", "prod"], message.Databases.Select(d => d.Name).OrderBy(n => n));
        Assert.Equal(id, message.Databases.Single(d => d.Name == "prod").ConnectionId);
    }

    [Fact]
    public async Task Deleting_a_chat_takes_its_messages_and_their_attachments_with_it()
    {
        var chat = await _chats.CreateChatAsync("t");
        await _chats.AppendMessageAsync(new ChatMessageInput(
            chat, ChatRole.User, "q", [new ChatDatabaseRef(Guid.NewGuid(), "prod")]));

        Assert.True(await _chats.DeleteChatAsync(chat));

        Assert.Null(await _chats.GetChatAsync(chat));
        Assert.Empty(await _db.ChatMessages.ToListAsync());
        Assert.Empty(await _db.ChatMessageDatabases.ToListAsync());
    }

    [Fact]
    public async Task Renaming_a_chat_changes_only_its_title()
    {
        var chat = await _chats.CreateChatAsync("first question, truncated");
        await _chats.AppendMessageAsync(new ChatMessageInput(chat, ChatRole.User, "q", []));

        Assert.True(await _chats.RenameChatAsync(chat, "quarterly revenue"));

        var detail = await _chats.GetChatAsync(chat);
        Assert.Equal("quarterly revenue", detail!.Title);
        Assert.Single(detail.Messages);
    }

    [Fact]
    public async Task Renaming_or_deleting_a_chat_that_is_gone_reports_it_rather_than_throwing()
    {
        // Two tabs, one chat: deleting it in the first leaves the second holding a stale row. That is an
        // ordinary race in a UI with a sidebar, not an exceptional condition.
        Assert.False(await _chats.RenameChatAsync(Guid.NewGuid(), "x"));
        Assert.False(await _chats.DeleteChatAsync(Guid.NewGuid()));
    }

    [Fact]
    public void A_title_is_the_first_message_cut_to_sixty_characters()
    {
        // There is no model to summarize with, so the first question is the title. 60 is what the
        // sidebar row fits before ellipsis.
        Assert.Equal("short one", ChatService.TitleFrom("  short one  "));
        Assert.Equal(60, ChatService.TitleFrom(new string('x', 200)).Length);
        Assert.Equal("Untitled chat", ChatService.TitleFrom("   "));
    }

    [Fact]
    public async Task An_error_answer_persists_its_code_and_the_metadata_but_no_rows()
    {
        // Rows are never stored (see the spec). What must survive is enough to render the answer again:
        // the code, the SQL, and the numbers.
        var chat = await _chats.CreateChatAsync("t");

        await _chats.AppendMessageAsync(new ChatMessageInput(
            chat, ChatRole.Assistant, "Query was canceled.", [],
            GeneratedSql: "SELECT 1", OutcomeKind: ChatOutcomeKind.Error,
            ErrorCode: "execution_canceled", RowCount: null, ElapsedMs: 12, Truncated: false));

        var message = (await _chats.GetChatAsync(chat))!.Messages.Single();
        Assert.Equal(ChatOutcomeKind.Error, message.OutcomeKind);
        Assert.Equal("execution_canceled", message.ErrorCode);
        Assert.Equal("SELECT 1", message.GeneratedSql);
        Assert.Equal(12, message.ElapsedMs);
    }

    public void Dispose()
    {
        _db.Dispose();
        _conn.Dispose();
    }
}
