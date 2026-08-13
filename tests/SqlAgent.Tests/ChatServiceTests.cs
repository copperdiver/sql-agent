using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
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

    [Fact]
    public async Task Appending_after_a_concurrent_writer_takes_the_next_sequence_retries_onto_a_fresh_one()
    {
        // Models the two-tab race the unique (ChatId, Sequence) index exists to catch: a second writer
        // claims the sequence this append is about to use, injected right before this attempt's own
        // SaveChanges runs — after it already read the old max, exactly the window the retry covers.
        // Asserting Sequence == 2 (not 1, the value this attempt originally computed) proves the retry
        // re-reads a fresh max rather than replaying the first attempt's stale tracked state; if it
        // didn't, the retried insert would collide again and this call would throw instead of returning.
        var chat = await _chats.CreateChatAsync("t");
        await _chats.AppendMessageAsync(new ChatMessageInput(chat, ChatRole.User, "q", [])); // sequence 0

        var options = new DbContextOptionsBuilder<SqlAgentDbContext>()
            .UseSqlite(_conn)
            .AddInterceptors(new ConcurrentSequenceInterceptor(_conn, chat, sequence: 1))
            .Options;
        await using var raceyDb = new SqlAgentDbContext(options);
        var raceyChats = new ChatService(raceyDb);

        var appended = await raceyChats.AppendMessageAsync(
            new ChatMessageInput(chat, ChatRole.Assistant, "a", []));

        Assert.Equal(2, appended.Sequence);
        var detail = await _chats.GetChatAsync(chat);
        Assert.Equal([0, 1, 2], detail!.Messages.Select(m => m.Sequence));
    }

    [Fact]
    public async Task A_chat_deleted_between_the_read_and_the_write_is_not_retried_and_surfaces_as_itself()
    {
        // SQLite reports the ChatMessages -> Chats foreign key under the same result code (19) as the
        // sequence collision above. Retrying it would re-read the now-missing chat and misreport this as
        // "Chat does not exist." instead of the constraint violation that actually happened — deleting a
        // chat while an append to it is in flight is the two-tab race this scenario models, so the
        // failure has to surface recognizably, not get swallowed into the wrong exception.
        var chat = await _chats.CreateChatAsync("t");

        var options = new DbContextOptionsBuilder<SqlAgentDbContext>()
            .UseSqlite(_conn)
            .AddInterceptors(new ChatDeletedMidSaveInterceptor(_conn, chat))
            .Options;
        await using var raceyDb = new SqlAgentDbContext(options);
        var raceyChats = new ChatService(raceyDb);

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() =>
            raceyChats.AppendMessageAsync(new ChatMessageInput(chat, ChatRole.User, "q", [])));
        Assert.IsType<SqliteException>(ex.InnerException);
    }

    [Fact]
    public async Task Duplicate_database_names_on_one_message_are_deduped_rather_than_retried()
    {
        // Two attachments sharing a name would hit the unique (ChatMessageId, DatabaseName) index every
        // time, since both always belong to whatever message id this attempt creates — retrying could
        // never help, unlike the sequence collision above. ChatService dedupes before writing instead, so
        // this never reaches the index, and the message ends up with one attachment, not a thrown
        // exception.
        var chat = await _chats.CreateChatAsync("t");
        var id = Guid.NewGuid();

        var message = await _chats.AppendMessageAsync(new ChatMessageInput(
            chat, ChatRole.User, "q", [new ChatDatabaseRef(id, "prod"), new ChatDatabaseRef(id, "prod")]));

        Assert.Equal(["prod"], message.Databases.Select(d => d.Name));
    }

    [Fact]
    public async Task A_chat_in_a_project_is_not_in_the_history_list()
    {
        // The sidebar shows each conversation in exactly one place. This is the store half of that rule;
        // ProjectServiceTests covers the round trip back out.
        var chat = await _chats.CreateChatAsync("grouped");
        var project = new Project
        {
            Id = Guid.NewGuid(), Name = "quarterly",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        _db.Projects.Add(project);
        await _db.SaveChangesAsync();
        (await _db.Chats.FirstAsync(c => c.Id == chat)).ProjectId = project.Id;
        await _db.SaveChangesAsync();

        Assert.DoesNotContain(await _chats.ListHistoryAsync(), c => c.Id == chat);
    }

    public void Dispose()
    {
        _db.Dispose();
        _conn.Dispose();
    }
}

/// <summary>
/// Fires once, immediately before the victim's own SaveChanges executes, and uses a second context on
/// the same open connection to insert a ChatMessage claiming the sequence the victim is about to write —
/// modelling a second browser tab that wins the race. Fires only once so a retry's own SaveChanges (the
/// second call on the victim context) goes through clean, the same as a real second attempt would.
/// </summary>
file sealed class ConcurrentSequenceInterceptor(SqliteConnection connection, Guid chatId, int sequence)
    : SaveChangesInterceptor
{
    private bool _fired;

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
    {
        if (!_fired)
        {
            _fired = true;
            var options = new DbContextOptionsBuilder<SqlAgentDbContext>().UseSqlite(connection).Options;
            await using var other = new SqlAgentDbContext(options);
            other.ChatMessages.Add(new ChatMessage
            {
                Id = Guid.NewGuid(),
                ChatId = chatId,
                Sequence = sequence,
                Role = ChatRole.User,
                Text = "interloper",
                CreatedAt = DateTime.UtcNow,
            });
            await other.SaveChangesAsync(ct);
        }
        return await base.SavingChangesAsync(eventData, result, ct);
    }
}

/// <summary>
/// Fires once, immediately before the victim's own SaveChanges executes, and deletes the chat through a
/// second context on the same open connection — modelling a second tab deleting the chat between the
/// victim's read and its write. The foreign-key violation this produces shares SQLite's result code with
/// the sequence collision above, which is exactly what the narrowed retry filter has to tell apart.
/// </summary>
file sealed class ChatDeletedMidSaveInterceptor(SqliteConnection connection, Guid chatId) : SaveChangesInterceptor
{
    private bool _fired;

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
    {
        if (!_fired)
        {
            _fired = true;
            var options = new DbContextOptionsBuilder<SqlAgentDbContext>().UseSqlite(connection).Options;
            await using var other = new SqlAgentDbContext(options);
            await other.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM Chats WHERE Id = {chatId}", ct);
        }
        return await base.SavingChangesAsync(eventData, result, ct);
    }
}
