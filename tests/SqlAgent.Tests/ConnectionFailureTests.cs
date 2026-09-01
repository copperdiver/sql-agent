using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SqlAgent.Core;
using SqlAgent.Host.Web;
using SqlAgent.Providers.Postgres;
using SqlAgent.Providers.SqlServer;
using SqlAgent.Storage;

namespace SqlAgent.Tests;

/// <summary>
/// A connection test that says only "it failed" makes a config page useless; one that repeats the
/// driver's sentence can echo the connection string back into the browser. The classifiers are the
/// middle: a fixed set of outcomes the UI has words for.
///
/// Pure functions over the pieces a provider extracts, not methods over the exception — SqlException
/// cannot be constructed outside its own assembly, so a classifier taking exceptions could not be tested
/// at all.
/// </summary>
public class ConnectionFailureTests
{
    [Theory]
    [InlineData("28P01", ConnectionFailure.AuthenticationFailed)]
    [InlineData("28000", ConnectionFailure.AuthenticationFailed)]
    [InlineData("3D000", ConnectionFailure.DatabaseNotFound)]
    [InlineData("53300", ConnectionFailure.Unknown)]
    [InlineData(null, ConnectionFailure.Unknown)]
    public void Postgres_states_map_to_outcomes(string? sqlState, ConnectionFailure expected)
        => Assert.Equal(expected, PostgresFailure.Classify(sqlState, isSocketFailure: false, isTimeout: false));

    [Fact]
    public void A_postgres_socket_failure_is_an_unreachable_host()
        => Assert.Equal(
            ConnectionFailure.HostUnreachable,
            PostgresFailure.Classify(null, isSocketFailure: true, isTimeout: false));

    [Fact]
    public void A_postgres_timeout_outranks_everything_else()
    {
        // A timeout that also produced a socket error is still a timeout: it is the fact the user can
        // act on, and "unreachable" would send them to check an address that is probably correct.
        Assert.Equal(
            ConnectionFailure.Timeout,
            PostgresFailure.Classify("28P01", isSocketFailure: true, isTimeout: true));
    }

    [Theory]
    [InlineData(18456, ConnectionFailure.AuthenticationFailed)]
    [InlineData(18452, ConnectionFailure.AuthenticationFailed)]
    [InlineData(4060, ConnectionFailure.DatabaseNotFound)]
    [InlineData(911, ConnectionFailure.DatabaseNotFound)]
    [InlineData(53, ConnectionFailure.HostUnreachable)]
    [InlineData(0, ConnectionFailure.HostUnreachable)]
    [InlineData(-1, ConnectionFailure.HostUnreachable)]
    [InlineData(258, ConnectionFailure.HostUnreachable)]
    [InlineData(10061, ConnectionFailure.HostUnreachable)]
    [InlineData(11001, ConnectionFailure.HostUnreachable)]
    [InlineData(-2, ConnectionFailure.Timeout)]
    [InlineData(50000, ConnectionFailure.Unknown)]
    [InlineData(null, ConnectionFailure.Unknown)]
    public void SqlServer_numbers_map_to_outcomes(int? number, ConnectionFailure expected)
        => Assert.Equal(expected, SqlServerFailure.Classify(number, isSocketFailure: false, isTimeout: false));

    [Fact]
    public void A_sqlserver_socket_failure_with_no_number_is_an_unreachable_host()
        => Assert.Equal(
            ConnectionFailure.HostUnreachable,
            SqlServerFailure.Classify(null, isSocketFailure: true, isTimeout: false));

    [Fact]
    public void Every_failure_has_a_code_and_a_sentence()
    {
        // A new member added without words for it would render an empty status line — the failure mode
        // that made the old "just show the driver text" behaviour feel necessary.
        foreach (var failure in Enum.GetValues<ConnectionFailure>().Where(f => f != ConnectionFailure.None))
        {
            Assert.False(string.IsNullOrWhiteSpace(ConnectionFailureText.Code(failure)), failure.ToString());
            Assert.False(string.IsNullOrWhiteSpace(ConnectionFailureText.Message(failure)), failure.ToString());
        }
    }

    [Fact]
    public void No_failure_sentence_repeats_driver_wording()
    {
        // The sentences are ours. If one is ever built from a Diagnostic, this is what catches it.
        foreach (var failure in Enum.GetValues<ConnectionFailure>().Where(f => f != ConnectionFailure.None))
            Assert.DoesNotContain("Exception", ConnectionFailureText.Message(failure), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_tester_logs_the_diagnostic_and_does_not_return_it()
    {
        // The boundary, asserted as a boundary: ConnectionTestOutcome has no field the text could travel
        // in, and the text is in the log instead.
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection("DataSource=:memory:");
        conn.Open();
        await using var db = new SqlAgentDbContext(
            new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<SqlAgentDbContext>()
                .UseSqlite(conn).Options);
        await db.Database.EnsureCreatedAsync();

        var provider = new FailingTestProvider(
            DatabaseProviderType.Postgres,
            ConnectionTestResult.Fail(
                ConnectionFailure.AuthenticationFailed,
                "28P01: password authentication failed for user \"sa\" (Host=secret-host;Password=hunter2)",
                7));
        var logs = new RecordingLoggerProvider();
        var tester = new ConnectionTester(
            new DatabaseConnectionService(db, new InMemorySecretStore()),
            new DatabaseProviderRegistry([provider]),
            new TypedLogger<ConnectionTester>(logs.CreateLogger("ConnectionTester")));

        var outcome = await tester.TestDraftAsync(DatabaseProviderType.Postgres, "Host=secret-host");

        Assert.False(outcome.Success);
        Assert.Equal(ConnectionFailure.AuthenticationFailed, outcome.Failure);
        Assert.DoesNotContain(
            outcome.GetType().GetProperties(),
            p => p.PropertyType == typeof(string) && p.Name == "Diagnostic");
        Assert.Contains(logs.Records, r => r.Message.Contains("hunter2", StringComparison.Ordinal));
    }
}

file sealed class FailingTestProvider(DatabaseProviderType type, ConnectionTestResult result) : IDatabaseProvider
{
    public DatabaseProviderType ProviderType => type;

    public Task<ConnectionTestResult> TestConnectionAsync(string cs, CancellationToken ct = default)
        => Task.FromResult(result);

    public Task<DatabaseSchema> GetSchemaAsync(string cs, CancellationToken ct = default)
        => Task.FromResult(new DatabaseSchema([]));

    public Task<QueryResultSet> ExecuteQueryAsync(
        string cs, string sql, QueryExecutionOptions options, CancellationToken ct = default)
        => throw new NotSupportedException();
}

/// <summary>Adapts a plain ILogger to ILogger&lt;T&gt; so RecordingLoggerProvider can be used where a
/// typed logger is required.</summary>
file sealed class TypedLogger<T>(ILogger inner) : ILogger<T>
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => inner.BeginScope(state);
    public bool IsEnabled(LogLevel level) => inner.IsEnabled(level);
    public void Log<TState>(
        LogLevel level, EventId id, TState state, Exception? ex, Func<TState, Exception?, string> formatter)
        => inner.Log(level, id, state, ex, formatter);
}
