using SqlAgent.Core;

namespace SqlAgent.Providers.Postgres;

/// <summary>
/// Maps what Npgsql reports about a failed connection onto <see cref="ConnectionFailure"/>. A pure
/// function over the pieces the provider pulled out of the exception, rather than a method over the
/// exception itself, so it can be unit-tested — the SQL Server twin has no choice, since SqlException
/// cannot be constructed outside its own assembly, and the two are kept symmetrical on purpose.
/// </summary>
public static class PostgresFailure
{
    public static ConnectionFailure Classify(string? sqlState, bool isSocketFailure, bool isTimeout)
    {
        // Timeout first: a request that timed out often also reports a socket error on the way out, and
        // "unreachable" would send the user to check an address that is probably right.
        if (isTimeout) return ConnectionFailure.Timeout;

        return sqlState switch
        {
            // 28P01 invalid_password, 28000 invalid_authorization_specification.
            "28P01" or "28000" => ConnectionFailure.AuthenticationFailed,
            // 3D000 invalid_catalog_name — the server is there, the database is not.
            "3D000" => ConnectionFailure.DatabaseNotFound,
            // A SQLSTATE at all means the server answered, so it was reachable whatever else went wrong.
            not null => ConnectionFailure.Unknown,
            _ => isSocketFailure ? ConnectionFailure.HostUnreachable : ConnectionFailure.Unknown,
        };
    }
}
