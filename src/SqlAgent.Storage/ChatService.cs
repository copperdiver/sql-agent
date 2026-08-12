using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace SqlAgent.Storage;

/// <summary>A database attached to a message: the live connection id when it still exists, and always
/// the name it had when the message was sent.</summary>
public record ChatDatabaseRef(Guid? ConnectionId, string Name);

/// <summary>A history row. Deliberately without messages — the sidebar lists hundreds of these.</summary>
public record ChatSummary(Guid Id, string Title, DateTime LastMessageAt);

/// <summary>One message as the UI renders it. Rows are absent because rows are never stored.</summary>
public record ChatMessageView(
    Guid Id,
    int Sequence,
    ChatRole Role,
    string Text,
    DateTime CreatedAt,
    string? GeneratedSql,
    ChatOutcomeKind OutcomeKind,
    string? ErrorCode,
    int? RowCount,
    long? ElapsedMs,
    bool Truncated,
    IReadOnlyList<ChatDatabaseRef> Databases);

/// <summary>A whole conversation, messages in order.</summary>
public record ChatDetail(Guid Id, string Title, IReadOnlyList<ChatMessageView> Messages);

/// <summary>What a caller supplies to append one message.</summary>
public record ChatMessageInput(
    Guid ChatId,
    ChatRole Role,
    string Text,
    IReadOnlyList<ChatDatabaseRef> Databases,
    string? GeneratedSql = null,
    ChatOutcomeKind OutcomeKind = ChatOutcomeKind.None,
    string? ErrorCode = null,
    int? RowCount = null,
    long? ElapsedMs = null,
    bool Truncated = false);

/// <summary>
/// The chat store: history, one conversation, and appends. Orchestrating a turn — deciding what to do
/// with the attached databases and calling the model — is <see cref="ChatTurnService"/>'s job, kept
/// separate so this stays a store with no opinion about language models.
/// </summary>
public class ChatService(SqlAgentDbContext db)
{
    /// <summary>SQLite's constraint-violation result code. Raised here by the unique (ChatId, Sequence)
    /// index when two circuits append to one chat at the same moment.</summary>
    private const int SqliteConstraint = 19;

    public async Task<IReadOnlyList<ChatSummary>> ListHistoryAsync(
        int take = 200, CancellationToken ct = default) =>
        await db.Chats
            .OrderByDescending(c => c.LastMessageAt)
            .Take(take)
            .Select(c => new ChatSummary(c.Id, c.Title, c.LastMessageAt))
            .ToListAsync(ct);

    public async Task<ChatDetail?> GetChatAsync(Guid id, CancellationToken ct = default)
    {
        var chat = await db.Chats
            .AsNoTracking()
            .Include(c => c.Messages).ThenInclude(m => m.Databases)
            .FirstOrDefaultAsync(c => c.Id == id, ct);
        if (chat is null) return null;

        return new ChatDetail(chat.Id, chat.Title,
            chat.Messages.OrderBy(m => m.Sequence).Select(ToView).ToList());
    }

    public async Task<Guid> CreateChatAsync(string title, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var chat = new Chat { Id = Guid.NewGuid(), Title = title, CreatedAt = now, UpdatedAt = now, LastMessageAt = now };
        db.Chats.Add(chat);
        await db.SaveChangesAsync(ct);
        return chat.Id;
    }

    public async Task<ChatMessageView> AppendMessageAsync(
        ChatMessageInput input, CancellationToken ct = default)
    {
        try
        {
            return await AppendOnceAsync(input, ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqliteException { SqliteErrorCode: SqliteConstraint })
        {
            // Another circuit took the sequence number between the read and the write. Re-reading the
            // max and trying again is enough: the loser of the race simply lands after the winner. One
            // retry, not a loop — a second collision means something other than a two-tab race, and a
            // retry loop would hide it.
            db.ChangeTracker.Clear();
            return await AppendOnceAsync(input, ct);
        }
    }

    private async Task<ChatMessageView> AppendOnceAsync(ChatMessageInput input, CancellationToken ct)
    {
        var chat = await db.Chats.FirstOrDefaultAsync(c => c.Id == input.ChatId, ct)
            ?? throw new InvalidOperationException($"Chat {input.ChatId} does not exist.");

        var next = await db.ChatMessages
            .Where(m => m.ChatId == input.ChatId)
            .Select(m => (int?)m.Sequence)
            .MaxAsync(ct) is { } max ? max + 1 : 0;

        var now = DateTime.UtcNow;
        var message = new ChatMessage
        {
            Id = Guid.NewGuid(),
            ChatId = input.ChatId,
            Sequence = next,
            Role = input.Role,
            Text = input.Text,
            CreatedAt = now,
            GeneratedSql = input.GeneratedSql,
            OutcomeKind = input.OutcomeKind,
            ErrorCode = input.ErrorCode,
            RowCount = input.RowCount,
            ElapsedMs = input.ElapsedMs,
            Truncated = input.Truncated,
            Databases = input.Databases.Select(d => new ChatMessageDatabase
            {
                Id = Guid.NewGuid(),
                DatabaseConnectionId = d.ConnectionId,
                DatabaseName = d.Name,
            }).ToList(),
        };

        db.ChatMessages.Add(message);
        chat.LastMessageAt = now;
        chat.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        return ToView(message);
    }

    public async Task<bool> RenameChatAsync(Guid id, string title, CancellationToken ct = default)
    {
        var chat = await db.Chats.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (chat is null) return false;
        chat.Title = TitleFrom(title);
        chat.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> DeleteChatAsync(Guid id, CancellationToken ct = default)
    {
        var chat = await db.Chats.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (chat is null) return false;
        // Messages and their attachment rows go with it through the cascade configured on the context.
        db.Chats.Remove(chat);
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>The title for a chat named after its first question. There is no model to summarize
    /// with, so this is a trim and a cut, not a summary.</summary>
    public static string TitleFrom(string firstMessage)
    {
        var trimmed = firstMessage.Trim();
        if (trimmed.Length == 0) return "Untitled chat";
        return trimmed.Length <= 60 ? trimmed : trimmed[..60];
    }

    private static ChatMessageView ToView(ChatMessage m) => new(
        m.Id, m.Sequence, m.Role, m.Text, m.CreatedAt, m.GeneratedSql, m.OutcomeKind,
        m.ErrorCode, m.RowCount, m.ElapsedMs, m.Truncated,
        m.Databases.Select(d => new ChatDatabaseRef(d.DatabaseConnectionId, d.DatabaseName)).ToList());
}
