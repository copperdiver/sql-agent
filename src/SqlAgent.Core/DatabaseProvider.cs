namespace SqlAgent.Core;

/// <summary>Database engine a connection targets. Drives provider selection (see CD-57).</summary>
public enum DatabaseProviderType
{
    SqlServer = 1,
    Postgres = 2,
}

/// <summary>
/// Why a connection test failed, in terms the UI has words for. A closed set on purpose: the driver's own
/// sentence can name the host, the database, the user, and — depending on the driver and the failure —
/// echo connection-string keywords back, so it is not something to render.
/// </summary>
public enum ConnectionFailure
{
    None = 0,
    AuthenticationFailed,
    DatabaseNotFound,
    HostUnreachable,
    Timeout,
    Unknown,
}

/// <summary>
/// Outcome of a connection test at the provider level. Never throws back to the caller for a
/// reachable-but-rejecting server. <see cref="Diagnostic"/> is the driver's own text and is for the log
/// only — <see cref="ConnectionTestOutcome"/> is what leaves Storage, and it has no field to carry it.
/// </summary>
public record ConnectionTestResult(
    bool Success,
    ConnectionFailure Failure,
    string? Diagnostic,
    string? ServerVersion = null,
    long ElapsedMs = 0)
{
    public static ConnectionTestResult Ok(string? serverVersion, long elapsedMs)
        => new(true, ConnectionFailure.None, null, serverVersion, elapsedMs);

    public static ConnectionTestResult Fail(ConnectionFailure failure, string diagnostic, long elapsedMs)
        => new(false, failure, diagnostic, null, elapsedMs);
}

/// <summary>
/// What a connection test looks like above Storage. Deliberately a different type from
/// <see cref="ConnectionTestResult"/> rather than the same one with a rule attached: the driver text is
/// absent because there is nowhere to put it, which is a guarantee a comment cannot make.
/// </summary>
public record ConnectionTestOutcome(
    bool Success, ConnectionFailure Failure, string? ServerVersion, long ElapsedMs);

/// <summary>
/// Per-dialect database driver: connection testing (CD-57) and schema extraction (CD-58 T4).
/// SQL parsing/normalization and execution land in later CD-50 tasks (T5–T6) on this same interface.
/// </summary>
public interface IDatabaseProvider
{
    DatabaseProviderType ProviderType { get; }

    /// <summary>Opens and closes a connection to verify the (draft or saved) connection string works.</summary>
    Task<ConnectionTestResult> TestConnectionAsync(string connectionString, CancellationToken ct = default);

    /// <summary>Reads tables, columns, primary keys, and foreign keys into the common <see cref="DatabaseSchema"/> (CD-50 T4).</summary>
    Task<DatabaseSchema> GetSchemaAsync(string connectionString, CancellationToken ct = default);

    /// <summary>
    /// Executes already policy-approved SQL (CD-50 T6) and returns the result set, honoring
    /// <see cref="QueryExecutionOptions.MaxRows"/> and the command timeout. Cancellation (timeout or
    /// caller) flows through <paramref name="ct"/> as <see cref="OperationCanceledException"/>.
    /// </summary>
    Task<QueryResultSet> ExecuteQueryAsync(
        string connectionString, string sql, QueryExecutionOptions options, CancellationToken ct = default);
}

/// <summary>Selects the <see cref="IDatabaseProvider"/> for a stored <see cref="DatabaseProviderType"/>.</summary>
public interface IDatabaseProviderRegistry
{
    IDatabaseProvider Get(DatabaseProviderType type);
}

/// <summary>Maps each registered provider by the type it reports. Built from the available providers.</summary>
public class DatabaseProviderRegistry : IDatabaseProviderRegistry
{
    private readonly IReadOnlyDictionary<DatabaseProviderType, IDatabaseProvider> _providers;

    public DatabaseProviderRegistry(IEnumerable<IDatabaseProvider> providers)
        => _providers = providers.ToDictionary(p => p.ProviderType);

    public IDatabaseProvider Get(DatabaseProviderType type)
        => _providers.TryGetValue(type, out var p)
            ? p
            : throw new NotSupportedException($"No database provider registered for {type}.");
}
