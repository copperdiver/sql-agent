using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SqlAgent.Core;
using SqlAgent.Providers.Postgres;
using SqlAgent.Providers.SqlServer;
using SqlAgent.Storage;

namespace SqlAgent.Tests;

/// <summary>Records the connection string it was handed and returns a canned result. No real I/O.</summary>
file sealed class FakeProvider(DatabaseProviderType type, ConnectionTestResult result) : IDatabaseProvider
{
    public string? LastConnectionString { get; private set; }
    public DatabaseProviderType ProviderType => type;

    public Task<ConnectionTestResult> TestConnectionAsync(string connectionString, CancellationToken ct = default)
    {
        LastConnectionString = connectionString;
        return Task.FromResult(result);
    }

    public Task<DatabaseSchema> GetSchemaAsync(string connectionString, CancellationToken ct = default)
        => throw new NotSupportedException();

    public Task<QueryResultSet> ExecuteQueryAsync(
        string connectionString, string sql, QueryExecutionOptions options, CancellationToken ct = default)
        => throw new NotSupportedException();
}

public class DatabaseProviderRegistryTests
{
    [Fact]
    public void Get_returns_provider_matching_the_stored_type()
    {
        var pg = new FakeProvider(DatabaseProviderType.Postgres, ConnectionTestResult.Ok(null, 0));
        var ss = new FakeProvider(DatabaseProviderType.SqlServer, ConnectionTestResult.Ok(null, 0));
        var registry = new DatabaseProviderRegistry([pg, ss]);

        Assert.Same(pg, registry.Get(DatabaseProviderType.Postgres));
        Assert.Same(ss, registry.Get(DatabaseProviderType.SqlServer));
    }

    [Fact]
    public void Get_throws_for_an_unregistered_type()
    {
        var registry = new DatabaseProviderRegistry([
            new FakeProvider(DatabaseProviderType.Postgres, ConnectionTestResult.Ok(null, 0))
        ]);

        Assert.Throws<NotSupportedException>(() => registry.Get(DatabaseProviderType.SqlServer));
    }

    [Fact]
    public void Real_providers_report_their_type()
    {
        Assert.Equal(DatabaseProviderType.Postgres, new PostgresProvider().ProviderType);
        Assert.Equal(DatabaseProviderType.SqlServer, new SqlServerProvider().ProviderType);
    }
}

public class ConnectionTesterTests
{
    private static (DatabaseConnectionService svc, SqliteConnection conn) NewService()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        var db = new SqlAgentDbContext(
            new DbContextOptionsBuilder<SqlAgentDbContext>().UseSqlite(conn).Options);
        db.Database.EnsureCreated();
        return (new DatabaseConnectionService(db, new InMemorySecretStore()), conn);
    }

    [Fact]
    public async Task TestDraft_routes_the_raw_string_to_the_typed_provider()
    {
        var (svc, conn) = NewService();
        var pg = new FakeProvider(DatabaseProviderType.Postgres, ConnectionTestResult.Ok("16.0", 5));
        var tester = new ConnectionTester(svc, new DatabaseProviderRegistry([pg]), NullLogger<ConnectionTester>.Instance);

        var result = await tester.TestDraftAsync(DatabaseProviderType.Postgres, "Host=draft");

        Assert.True(result.Success);
        Assert.Equal("Host=draft", pg.LastConnectionString);
        conn.Dispose();
    }

    [Fact]
    public async Task TestSaved_resolves_the_secret_and_uses_the_stored_provider_type()
    {
        var (svc, conn) = NewService();
        var created = await svc.CreateAsync(
            new DatabaseConnectionInput("c", DatabaseProviderType.SqlServer, false), "Server=saved;Password=p");
        var ss = new FakeProvider(DatabaseProviderType.SqlServer, ConnectionTestResult.Ok("16", 3));
        var pg = new FakeProvider(
            DatabaseProviderType.Postgres,
            ConnectionTestResult.Fail(ConnectionFailure.Unknown, "wrong provider", 0));
        var tester = new ConnectionTester(svc, new DatabaseProviderRegistry([ss, pg]), NullLogger<ConnectionTester>.Instance);

        var result = await tester.TestSavedAsync(created.Id);

        Assert.NotNull(result);
        Assert.True(result!.Success);
        Assert.Equal("Server=saved;Password=p", ss.LastConnectionString); // resolved from the secret store
        Assert.Null(pg.LastConnectionString);                              // Postgres provider not selected
        conn.Dispose();
    }

    [Fact]
    public async Task TestSaved_returns_null_for_an_unknown_id()
    {
        var (svc, conn) = NewService();
        var tester = new ConnectionTester(svc, new DatabaseProviderRegistry([
            new FakeProvider(DatabaseProviderType.SqlServer, ConnectionTestResult.Ok(null, 0))
        ]), NullLogger<ConnectionTester>.Instance);

        Assert.Null(await tester.TestSavedAsync(Guid.NewGuid()));
        conn.Dispose();
    }
}

/// <summary>
/// Failure-path coverage against a closed local port: a refused connection must surface as a failed
/// <see cref="ConnectionTestResult"/>, never an exception. Success paths need a live SQL Server /
/// PostgreSQL instance — run them as documented local fixtures, not in unit CI.
/// </summary>
public class ProviderConnectionFailureTests
{
    [Fact]
    public async Task Postgres_returns_failure_for_a_refused_connection()
    {
        var result = await new PostgresProvider().TestConnectionAsync(
            "Host=127.0.0.1;Port=1;Username=u;Password=p;Database=d;Timeout=2;Command Timeout=2");

        Assert.False(result.Success);
        Assert.False(string.IsNullOrEmpty(result.Diagnostic));
    }

    [Fact]
    public async Task SqlServer_returns_failure_for_a_refused_connection()
    {
        var result = await new SqlServerProvider().TestConnectionAsync(
            "Server=127.0.0.1,1;Database=d;User Id=u;Password=p;Connect Timeout=2;Encrypt=false;TrustServerCertificate=true");

        Assert.False(result.Success);
        Assert.False(string.IsNullOrEmpty(result.Diagnostic));
    }
}
