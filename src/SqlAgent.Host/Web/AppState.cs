using SqlAgent.Storage;

namespace SqlAgent.Host.Web;

/// <summary>
/// Which connection the workspace is pointed at. Scoped to the circuit, so it is per browser tab.
/// The rail, the SQL tab, and the chat tab all read it, so it lives here rather than in a parent
/// component's parameters.
/// </summary>
public sealed class AppState
{
    public DatabaseConnectionInfo? Connection { get; private set; }
    public Guid? ConnectionId => Connection?.Id;

    /// <summary>Raised when the selected connection changes — a different row, or the same row with
    /// different fields after an edit, or nothing selected at all.</summary>
    public event Action? Changed;

    /// <summary>
    /// Raised when the set of saved connections changes: a create, an edit, or a delete on the
    /// Connections page. <see cref="Changed"/> is not a substitute — it only fires when the
    /// <em>selection</em> moves, so a connection created while some other one is selected (or none)
    /// would never reach the rail's picker, which reads its list once on mount and then lives in
    /// MainLayout for the rest of the circuit.
    /// </summary>
    public event Action? ConnectionsChanged;

    public void Select(DatabaseConnectionInfo? connection)
    {
        // Value equality, not id equality: DatabaseConnectionInfo is a record, so re-selecting the
        // same row with the same fields is still a no-op, but re-selecting it after an edit (renamed,
        // read-only flipped) now notifies. Comparing ids alone silently swallowed that case, which is
        // why editing the selected connection updated nothing anywhere in the UI.
        if (Connection == connection) return;
        Connection = connection;
        Changed?.Invoke();
    }

    /// <summary>Announces a create/edit/delete against the saved-connection set.</summary>
    public void NotifyConnectionsChanged() => ConnectionsChanged?.Invoke();

    /// <summary>Which chat the page is showing, so the sidebar can highlight its row. Null on a new,
    /// unsaved chat — the row does not exist until the first message is sent.</summary>
    public Guid? ActiveChatId { get; private set; }

    /// <summary>
    /// Raised when the history list needs re-reading: a chat was created, renamed, deleted, or a message
    /// moved one to the top. Separate from the selection moving, for the same reason
    /// <see cref="ConnectionsChanged"/> is separate from <see cref="Changed"/> — the sidebar section is a
    /// sibling of the page, not a child, and nothing else would ever tell it.
    /// </summary>
    public event Action? ChatsChanged;

    public void SetActiveChat(Guid? chatId)
    {
        // Every render of /chat/{id} sets this, including re-renders of the chat already open. Firing on
        // an unchanged value would re-query the whole history list from SQLite for nothing.
        if (ActiveChatId == chatId) return;
        ActiveChatId = chatId;
        ChatsChanged?.Invoke();
    }

    /// <summary>Announces a create, rename, delete, or a new message, without moving the selection.</summary>
    public void NotifyChatsChanged() => ChatsChanged?.Invoke();

    private string? _pendingSql;

    /// <summary>Hands generated SQL to the /sql page across a navigation. The page and the chat are
    /// separate routes, so there is no parameter to pass it through.</summary>
    public void HandOffSql(string sql) => _pendingSql = sql;

    /// <summary>Reads the handed-off SQL and clears it. Clearing is the point: without it, every later
    /// visit to /sql would overwrite whatever the user had typed with the same stale query.</summary>
    public string? TakePendingSql()
    {
        var sql = _pendingSql;
        _pendingSql = null;
        return sql;
    }
}
