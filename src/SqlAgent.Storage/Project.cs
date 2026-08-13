namespace SqlAgent.Storage;

/// <summary>
/// A folder for chats. A chat belongs to at most one project, and a chat that belongs to one leaves the
/// history list — the sidebar shows each conversation in exactly one place, so "move to project" reads
/// literally rather than adding a second copy somewhere else.
/// </summary>
public class Project
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>Its chats. Present so the chat count is one grouped query rather than one query per
    /// project, and so the delete path can decide their fate explicitly.</summary>
    public List<Chat> Chats { get; set; } = [];
}
