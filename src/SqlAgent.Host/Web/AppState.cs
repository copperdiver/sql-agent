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

    public event Action? Changed;

    public void Select(DatabaseConnectionInfo? connection)
    {
        if (Connection?.Id == connection?.Id) return;
        Connection = connection;
        Changed?.Invoke();
    }
}
