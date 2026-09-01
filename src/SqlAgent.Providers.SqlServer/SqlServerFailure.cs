using SqlAgent.Core;

namespace SqlAgent.Providers.SqlServer;

/// <summary>
/// Maps what SqlClient reports about a failed connection onto <see cref="ConnectionFailure"/>. Pure by
/// necessity: SqlException has no public constructor, so a classifier taking the exception could never be
/// exercised by a unit test, only by a live server.
/// </summary>
public static class SqlServerFailure
{
    public static ConnectionFailure Classify(int? number, bool isSocketFailure, bool isTimeout)
    {
        if (isTimeout || number == -2) return ConnectionFailure.Timeout;

        return number switch
        {
            // 18456 login failed, 18452 login from an untrusted domain.
            18456 or 18452 => ConnectionFailure.AuthenticationFailed,
            // 4060 cannot open database, 911 database does not exist.
            4060 or 911 => ConnectionFailure.DatabaseNotFound,
            // 0 is the number SqlClient uses for a transport-level failure without a server error
            // number. 2 server not found, 53 network path not found, 10060/10061 refused or timed out
            // at the socket, 11001 host not found, 40615 Azure firewall.
            -1 or 0 or 2 or 53 or 258 or 10060 or 10061 or 11001 or 40615 => ConnectionFailure.HostUnreachable,
            null => isSocketFailure ? ConnectionFailure.HostUnreachable : ConnectionFailure.Unknown,
            _ => ConnectionFailure.Unknown,
        };
    }
}
