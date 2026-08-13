using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SqlAgent.Core;
using SqlAgent.Storage;

namespace SqlAgent.Tests;

/// <summary>
/// Executor provider double: returns a canned result set, or runs a supplied behavior (e.g. an
/// infinite delay) so timeout / cancellation paths can be driven. Records whether it was ever called,
/// proving policy-denied SQL never reaches execution.
/// </summary>
file sealed class ExecFakeProvider(
    DatabaseProviderType type,
    QueryResultSet? result = null,
    Func<QueryExecutionOptions, CancellationToken, Task<QueryResultSet>>? behavior = null) : IDatabaseProvider
{
    public bool WasCalled { get; private set; }
    public DatabaseProviderType ProviderType => type;

    public Task<ConnectionTestResult> TestConnectionAsync(string connectionString, CancellationToken ct = default)
        => Task.FromResult(ConnectionTestResult.Ok(null, 0));

    public Task<DatabaseSchema> GetSchemaAsync(string connectionString, CancellationToken ct = default)
        => throw new NotSupportedException();

    public Task<QueryResultSet> ExecuteQueryAsync(
        string connectionString, string sql, QueryExecutionOptions options, CancellationToken ct = default)
    {
        WasCalled = true;
        if (behavior is not null) return behavior(options, ct);
        return Task.FromResult(result ?? new QueryResultSet([], [], false));
    }
}

public class QueryExecutionServiceTests
{
    private static (SqlAgentDbContext db, SqliteConnection conn) NewStore()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        var db = new SqlAgentDbContext(
            new DbContextOptionsBuilder<SqlAgentDbContext>().UseSqlite(conn).Options);
        db.Database.EnsureCreated();
        return (db, conn);
    }

    private static async Task<(QueryExecutionService svc, Guid connId)> SetupAsync(
        SqlAgentDbContext db,
        IDatabaseProvider provider,
        bool isReadOnly = true,
        QueryExecutionOptions? options = null)
    {
        var connections = new DatabaseConnectionService(db, new InMemorySecretStore());
        var created = await connections.CreateAsync(
            new DatabaseConnectionInput("c", provider.ProviderType, isReadOnly), "conn-string");
        var registry = new DatabaseProviderRegistry([provider]);
        var svc = new QueryExecutionService(
            connections, registry, db, NullLogger<QueryExecutionService>.Instance, options);
        return (svc, created.Id);
    }

    private static async Task<List<QueryAuditLog>> AuditAsync(SqlAgentDbContext db)
        => await db.QueryAuditLogs.ToListAsync();

    [Fact]
    public async Task Successful_query_returns_metadata_and_audits_without_rows()
    {
        var (db, conn) = NewStore();
        var resultSet = new QueryResultSet(["id", "name"], [new object?[] { 1, "a" }], Truncated: false);
        var (svc, connId) = await SetupAsync(db, new ExecFakeProvider(DatabaseProviderType.Postgres, resultSet));

        var r = await svc.ExecuteSqlAsync(connId, "SELECT id, name FROM orders");

        Assert.True(r.Success);
        Assert.Null(r.ErrorCode);
        Assert.Equal(["id", "name"], r.Columns);
        Assert.Equal(1, r.RowCount);
        Assert.False(r.Truncated);
        Assert.Equal("SELECT id, name FROM orders", r.Sql);

        var audit = Assert.Single(await AuditAsync(db));
        Assert.Equal("allow", audit.Decision);
        Assert.Equal(1, audit.RowCount);
        Assert.NotNull(audit.DurationMs);
        Assert.NotNull(audit.NormalizedSql);     // canonical re-render present
        Assert.Null(audit.DenyReason);
        // The audit entity has no column for result rows; the returned data must not be persisted.
        Assert.DoesNotContain("\"name\"", System.Text.Json.JsonSerializer.Serialize(audit));

        conn.Dispose();
    }

    [Fact]
    public async Task Hidden_table_is_denied_before_execution_and_audited()
    {
        var (db, conn) = NewStore();
        var provider = new ExecFakeProvider(DatabaseProviderType.Postgres);
        var (svc, connId) = await SetupAsync(db, provider);

        db.TablePolicies.Add(new TablePolicy
        {
            Id = Guid.NewGuid(),
            DatabaseConnectionId = connId,
            SchemaName = "public",
            TableName = "secrets",
            IsVisible = false,
        });
        await db.SaveChangesAsync();

        var r = await svc.ExecuteSqlAsync(connId, "SELECT * FROM secrets");

        Assert.False(r.Success);
        Assert.Equal("policy_denied_hidden_table", r.ErrorCode);
        Assert.False(provider.WasCalled);        // never opened execution

        var audit = Assert.Single(await AuditAsync(db));
        Assert.Equal("deny", audit.Decision);
        Assert.NotNull(audit.DenyReason);
        Assert.Null(audit.RowCount);

        conn.Dispose();
    }

    [Fact]
    public async Task Write_on_readonly_connection_is_denied_before_execution()
    {
        var (db, conn) = NewStore();
        var provider = new ExecFakeProvider(DatabaseProviderType.Postgres);
        var (svc, connId) = await SetupAsync(db, provider, isReadOnly: true);

        var r = await svc.ExecuteSqlAsync(connId, "UPDATE orders SET total = 0");

        Assert.False(r.Success);
        Assert.Equal("policy_denied_readonly", r.ErrorCode);
        Assert.False(provider.WasCalled);

        conn.Dispose();
    }

    [Fact]
    public async Task Unknown_connection_returns_error_without_audit()
    {
        var (db, conn) = NewStore();
        var connections = new DatabaseConnectionService(db, new InMemorySecretStore());
        var registry = new DatabaseProviderRegistry([new ExecFakeProvider(DatabaseProviderType.Postgres)]);
        var svc = new QueryExecutionService(connections, registry, db, NullLogger<QueryExecutionService>.Instance);

        var r = await svc.ExecuteSqlAsync(Guid.NewGuid(), "SELECT 1");

        Assert.False(r.Success);
        Assert.Equal("connection_not_found", r.ErrorCode);
        Assert.Empty(await AuditAsync(db));

        conn.Dispose();
    }

    [Fact]
    public async Task Timeout_yields_timeout_code_and_audits_error()
    {
        var (db, conn) = NewStore();
        // Provider blocks on the supplied token; the service's CancelAfter trips it.
        var provider = new ExecFakeProvider(
            DatabaseProviderType.Postgres,
            behavior: async (_, ct) =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                return new QueryResultSet([], [], false);
            });
        var options = new QueryExecutionOptions { Timeout = TimeSpan.FromMilliseconds(100) };
        var (svc, connId) = await SetupAsync(db, provider, options: options);

        var r = await svc.ExecuteSqlAsync(connId, "SELECT * FROM orders");

        Assert.False(r.Success);
        Assert.Equal("execution_timeout", r.ErrorCode);

        var audit = Assert.Single(await AuditAsync(db));
        Assert.Equal("error", audit.Decision);

        conn.Dispose();
    }

    [Fact]
    public async Task Caller_cancellation_yields_canceled_code_and_still_audits()
    {
        var (db, conn) = NewStore();
        var provider = new ExecFakeProvider(
            DatabaseProviderType.Postgres,
            behavior: async (_, ct) =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                return new QueryResultSet([], [], false);
            });
        var (svc, connId) = await SetupAsync(db, provider);

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(100));

        var r = await svc.ExecuteSqlAsync(connId, "SELECT * FROM orders", cts.Token);

        Assert.False(r.Success);
        Assert.Equal("execution_canceled", r.ErrorCode);

        // Audit must persist even though the caller's token was cancelled.
        var audit = Assert.Single(await AuditAsync(db));
        Assert.Equal("error", audit.Decision);

        conn.Dispose();
    }

    [Fact]
    public async Task Provider_error_is_mapped_to_execution_error_and_audited()
    {
        var (db, conn) = NewStore();
        var provider = new ExecFakeProvider(
            DatabaseProviderType.Postgres,
            behavior: (_, _) => throw new InvalidOperationException("boom"));
        var (svc, connId) = await SetupAsync(db, provider);

        var r = await svc.ExecuteSqlAsync(connId, "SELECT * FROM orders");

        Assert.False(r.Success);
        Assert.Equal("execution_error", r.ErrorCode);
        // The driver's own text ("boom") must never reach the caller — it can echo a connection string.
        Assert.Equal("The query could not be executed. The details are in the server log.", r.ErrorMessage);

        var audit = Assert.Single(await AuditAsync(db));
        Assert.Equal("error", audit.Decision);

        conn.Dispose();
    }
}
