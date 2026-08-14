using SqlAgent.Storage;

namespace SqlAgent.Host.Web;

/// <summary>How the last connection test in this session went. There is no background poller, so
/// "untested" is the honest answer until somebody presses the button.</summary>
public enum ConnectionStatus
{
    Untested,
    Ok,
    Failed,
}

/// <summary>
/// Which connection the workspace is pointed at. Scoped to the circuit, so it is per browser tab.
/// The SQL page owns both the picker and the state it sets, and it lives here rather than as a
/// field on that page because Select's value-equality check (below) is what makes an edit made on
/// the config page, a sibling route in the same circuit, visible on the SQL page without a
/// round trip through props. The chat page does not read it at all — it attaches databases
/// independently, through the composer's attachment menu.
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
    /// /database config page. <see cref="Changed"/> is not a substitute — it only fires when the
    /// <em>selection</em> moves, so a connection created or deleted while the SQL page is mounted
    /// would never reach its picker, which reads its list once on mount and does not otherwise
    /// re-read it for the rest of that page's lifetime.
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

    private readonly Dictionary<Guid, ConnectionStatus> _connectionStatus = [];

    /// <summary>
    /// The dot beside a database in the sidebar. Scoped to the circuit like everything else here, which
    /// is deliberate: a status persisted across restarts would claim a database was reachable at a moment
    /// nobody checked, and a background poller against arbitrary remote servers is a feature nobody asked
    /// for. Unknown until this session tested it.
    /// </summary>
    public ConnectionStatus StatusOf(Guid connectionId)
        => _connectionStatus.GetValueOrDefault(connectionId, ConnectionStatus.Untested);

    /// <summary>Raised when a dot should change. Separate from <see cref="ConnectionsChanged"/> because
    /// the set of databases has not moved — only what is known about one of them.</summary>
    public event Action? ConnectionStatusChanged;

    /// <summary>Records the result of a test. Silent on an unchanged value: the section re-reads its whole
    /// list from SQLite on this event, and pressing Test twice on a working connection should not cost
    /// two queries and two renders.</summary>
    public void RecordTest(Guid connectionId, bool success)
    {
        var next = success ? ConnectionStatus.Ok : ConnectionStatus.Failed;
        if (_connectionStatus.TryGetValue(connectionId, out var current) && current == next) return;
        _connectionStatus[connectionId] = next;
        ConnectionStatusChanged?.Invoke();
    }

    /// <summary>Drops what was known about a connection — called when one is deleted, so a later
    /// connection reusing the id (or a stale render) cannot inherit its dot.</summary>
    public void ForgetStatus(Guid connectionId)
    {
        if (_connectionStatus.Remove(connectionId))
            ConnectionStatusChanged?.Invoke();
    }

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

    /// <summary>A project the sidebar should open, set by a search hit. There is no project route to
    /// navigate to, so this is how a hit reaches a section that is not on the navigation path.</summary>
    public Guid? ProjectToExpand { get; private set; }

    public void RequestProjectExpanded(Guid projectId)
    {
        ProjectToExpand = projectId;
        ChatsChanged?.Invoke();
    }

    /// <summary>Reads the request and clears it, so re-rendering the sidebar for an unrelated reason does
    /// not keep re-opening a project the user has since collapsed.</summary>
    public Guid? TakeProjectToExpand()
    {
        var id = ProjectToExpand;
        ProjectToExpand = null;
        return id;
    }

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
