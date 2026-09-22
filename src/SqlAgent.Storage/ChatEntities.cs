namespace SqlAgent.Storage;

/// <summary>Who wrote a message.</summary>
public enum ChatRole { User, Assistant }

/// <summary>
/// What an assistant message carries. ConfirmationRequired and SchemaDiagram are persisted outcomes
/// whose rendering components live in Phase D.
/// </summary>
public enum ChatOutcomeKind { None, QueryResult, Clarification, ConfirmationRequired, Error, SchemaDiagram }

/// <summary>A conversation. Databases belong to its messages, not to it.</summary>
public class Chat
{
    public Guid Id { get; set; }
    public string Title { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime LastMessageAt { get; set; }
    public List<ChatMessage> Messages { get; set; } = [];

    /// <summary>The project this chat lives in, or null for an ungrouped chat — which is what the
    /// history list shows. A chat is never in both places.</summary>
    public Guid? ProjectId { get; set; }
    public Project? Project { get; set; }
}

/// <summary>
/// One message. Result rows are deliberately absent — <see cref="RowCount"/>, <see cref="ElapsedMs"/>
/// and <see cref="Truncated"/> are what a reloaded transcript shows instead, so the local store never
/// becomes a shadow copy of production data.
/// </summary>
public class ChatMessage
{
    public Guid Id { get; set; }
    public Guid ChatId { get; set; }
    public Chat? Chat { get; set; }

    /// <summary>Zero-based position in the conversation. Ordering by CreatedAt is not enough: a question
    /// and its answer are written back to back and can share a millisecond.</summary>
    public int Sequence { get; set; }

    public ChatRole Role { get; set; }
    public string Text { get; set; } = "";
    public DateTime CreatedAt { get; set; }

    public string? GeneratedSql { get; set; }
    public ChatOutcomeKind OutcomeKind { get; set; }

    /// <summary>Stable code from the service layer. The user-safe message lives in <see cref="Text"/>.</summary>
    public string? ErrorCode { get; set; }

    /// <summary>Operation label shown by the confirmation UI for a pending model write/DDL.</summary>
    public string? ConfirmationOperation { get; set; }

    /// <summary>Connection used to build a persisted schema diagram. The schema itself is never stored;
    /// reopening the transcript re-reads it through the visibility policy.</summary>
    public Guid? SchemaDiagramConnectionId { get; set; }

    public int? RowCount { get; set; }
    public long? ElapsedMs { get; set; }
    public bool Truncated { get; set; }

    public List<ChatMessageDatabase> Databases { get; set; } = [];
    public List<MessageAttachment> Attachments { get; set; } = [];
}

/// <summary>
/// One database attached to one message.
///
/// <see cref="DatabaseName"/> is the source of truth for what a question was asked against — it is
/// never null and never rewritten. <see cref="DatabaseConnectionId"/> is a historical value, captured at
/// send time and never cleaned up afterward: nothing nulls it when the connection is renamed or deleted,
/// so a non-null id is not proof the connection still exists. No consumer may read "id is not null" as
/// "the connection is still there" — resolve the id against <c>DatabaseConnections</c> and fall back to
/// the name when it does not resolve.
/// </summary>
public class ChatMessageDatabase
{
    public Guid Id { get; set; }
    public Guid ChatMessageId { get; set; }
    public ChatMessage? Message { get; set; }
    public Guid? DatabaseConnectionId { get; set; }
    public string DatabaseName { get; set; } = "";
}

/// <summary>Persisted metadata for one file attached to a message. File bytes live behind the provider boundary.</summary>
public class MessageAttachment
{
    public Guid Id { get; set; }
    public Guid ChatMessageId { get; set; }
    public ChatMessage? Message { get; set; }
    public string FileName { get; set; } = "";
    public string ContentType { get; set; } = "application/octet-stream";
    public long SizeBytes { get; set; }
    public string ProviderKey { get; set; } = "";
    public string StorageKey { get; set; } = "";
    public string Url { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}
