namespace SqlAgent.Storage;

/// <summary>Who wrote a message.</summary>
public enum ChatRole { User, Assistant }

/// <summary>
/// What an assistant message carries. Only the values Phase B1 can produce: Phase D adds
/// ConfirmationRequired and SchemaDiagram alongside the components that render them, so no member here
/// is ever written by nothing.
/// </summary>
public enum ChatOutcomeKind { None, QueryResult, Clarification, Error }

/// <summary>A conversation. Databases belong to its messages, not to it.</summary>
public class Chat
{
    public Guid Id { get; set; }
    public string Title { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime LastMessageAt { get; set; }
    public List<ChatMessage> Messages { get; set; } = [];
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

    public int? RowCount { get; set; }
    public long? ElapsedMs { get; set; }
    public bool Truncated { get; set; }

    public List<ChatMessageDatabase> Databases { get; set; } = [];
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
