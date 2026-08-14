using SqlAgent.Core;

namespace SqlAgent.Host.Web;

/// <summary>
/// The stable code and the sentence each connection-test failure renders with. One place, because two
/// surfaces show these and a page that invents its own wording is how a code and its message drift apart.
/// Every sentence is written here rather than derived from anything the driver said.
/// </summary>
public static class ConnectionFailureText
{
    public static string Code(ConnectionFailure failure) => failure switch
    {
        ConnectionFailure.AuthenticationFailed => "connection_test_auth_failed",
        ConnectionFailure.DatabaseNotFound => "connection_test_database_not_found",
        ConnectionFailure.HostUnreachable => "connection_test_host_unreachable",
        ConnectionFailure.Timeout => "connection_test_timeout",
        _ => "connection_test_failed",
    };

    public static string Message(ConnectionFailure failure) => failure switch
    {
        ConnectionFailure.AuthenticationFailed =>
            "The server rejected the credentials in the connection string.",
        ConnectionFailure.DatabaseNotFound =>
            "The server answered, but it has no database by that name.",
        ConnectionFailure.HostUnreachable =>
            "The server could not be reached at that address.",
        ConnectionFailure.Timeout =>
            "The server did not answer before the connection timed out.",
        // Deliberately points at the log rather than saying nothing: the detail exists, it is simply not
        // safe to put in a browser.
        _ => "The connection failed. The details are in the server log.",
    };
}
