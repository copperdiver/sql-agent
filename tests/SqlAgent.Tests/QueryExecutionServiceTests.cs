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
    Func<QueryExecutionOptions, CancellationToken, Task<QueryResultSet>>? behavior = null,
    DatabaseSchema? schema = null,
    Exception? schemaFailure = null) : IDatabaseProvider
{
    public bool WasCalled { get; private set; }
    public DatabaseProviderType ProviderType => type;

    public Task<ConnectionTestResult> TestConnectionAsync(string connectionString, CancellationToken ct = default)
        => Task.FromResult(ConnectionTestResult.Ok(null, 0));

    public Task<DatabaseSchema> GetSchemaAsync(string connectionString, CancellationToken ct = default)
        => schemaFailure is not null
            ? Task.FromException<DatabaseSchema>(schemaFailure)
            : Task.FromResult(schema ?? new DatabaseSchema([]));

    public Task<QueryResultSet> ExecuteQueryAsync(
        string connectionString, string sql, QueryExecutionOptions options, CancellationToken ct = default)
    {
        WasCalled = true;
        if (behavior is not null) return behavior(options, ct);
        return Task.FromResult(result ?? new QueryResultSet([], [], false));
    }
}

/// <summary>Provider double that counts <see cref="GetSchemaAsync"/> calls, proving the resolver reuses
/// SchemaCache rather than re-extracting the schema on every query.</summary>
file sealed class CountingSchemaProvider(DatabaseProviderType type, DatabaseSchema schema) : IDatabaseProvider
{
    public int SchemaReads { get; private set; }
    public DatabaseProviderType ProviderType => type;

    public Task<ConnectionTestResult> TestConnectionAsync(string cs, CancellationToken ct = default)
        => Task.FromResult(ConnectionTestResult.Ok(null, 0));

    public Task<DatabaseSchema> GetSchemaAsync(string cs, CancellationToken ct = default)
    {
        SchemaReads++;
        return Task.FromResult(schema);
    }

    public Task<QueryResultSet> ExecuteQueryAsync(
        string cs, string sql, QueryExecutionOptions options, CancellationToken ct = default)
        => Task.FromResult(new QueryResultSet([], [], false));
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
        var schemas = new SchemaService(connections, registry, db);
        var svc = new QueryExecutionService(
            connections, registry, db, schemas, NullLogger<QueryExecutionService>.Instance, options);
        return (svc, created.Id);
    }

    private static async Task<List<QueryAuditLog>> AuditAsync(SqlAgentDbContext db)
        => await db.QueryAuditLogs.ToListAsync();

    private static DatabaseSchema OrdersAndSummary() => new(
        [new SchemaTable("dbo", "orders", [new SchemaColumn("id", "int", false)], ["id"], [], []),
         new SchemaTable("dbo", "staging_orders", [new SchemaColumn("id", "int", false)], [], [], [])],
        [new SchemaView("dbo", "order_summary", [new SchemaColumn("id", "int", false)])]);

    private async Task SetLevelAsync(SqlAgentDbContext db, Guid connId, string name, bool visible, bool write)
    {
        db.TablePolicies.Add(new TablePolicy
        {
            Id = Guid.NewGuid(),
            DatabaseConnectionId = connId,
            SchemaName = "dbo",
            TableName = name,
            IsVisible = visible,
            CanRead = true,
            CanWrite = write,
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task A_write_to_a_read_only_object_is_denied_and_never_executes()
    {
        var (db, conn) = NewStore();
        var provider = new ExecFakeProvider(DatabaseProviderType.Postgres, schema: OrdersAndSummary());
        var (svc, connId) = await SetupAsync(db, provider, isReadOnly: false);
        await SetLevelAsync(db, connId, "orders", visible: true, write: false);

        var r = await svc.ExecuteSqlAsync(connId, "UPDATE orders SET total = 0");

        Assert.False(r.Success);
        Assert.Equal("policy_denied_readonly_object", r.ErrorCode);
        Assert.False(provider.WasCalled);
        Assert.Equal("deny", Assert.Single(await AuditAsync(db)).Decision);
        conn.Dispose();
    }

    [Fact]
    public async Task A_write_through_an_alias_declared_in_from_is_denied_by_the_real_objects_level()
    {
        // The end-to-end shape of the alias hole: the engine would apply this to `orders`, so the policy
        // has to check `orders` and not the alias standing in front of it. An alias has no policy row, so
        // before the collector resolved it this reached the provider on a writable connection.
        var (db, conn) = NewStore();
        var provider = new ExecFakeProvider(DatabaseProviderType.Postgres, schema: OrdersAndSummary());
        var (svc, connId) = await SetupAsync(db, provider, isReadOnly: false);
        await SetLevelAsync(db, connId, "orders", visible: true, write: false);

        var r = await svc.ExecuteSqlAsync(connId, "UPDATE o SET total = 0 FROM orders o");

        Assert.False(r.Success);
        Assert.Equal("policy_denied_readonly_object", r.ErrorCode);
        Assert.False(provider.WasCalled);
        conn.Dispose();
    }

    [Fact]
    public async Task Reading_a_read_only_object_inside_a_write_still_executes()
    {
        var (db, conn) = NewStore();
        var provider = new ExecFakeProvider(DatabaseProviderType.Postgres, schema: OrdersAndSummary());
        var (svc, connId) = await SetupAsync(db, provider, isReadOnly: false);
        await SetLevelAsync(db, connId, "staging_orders", visible: true, write: false);

        var r = await svc.ExecuteSqlAsync(connId, "INSERT INTO orders (id) SELECT id FROM staging_orders");

        Assert.True(r.Success);
        Assert.True(provider.WasCalled);
        conn.Dispose();
    }

    [Fact]
    public async Task A_write_to_a_view_is_denied_as_a_view_write()
    {
        // The view has no policy row at all — it is fully accessible by the phase's own default rule —
        // so the only thing that can refuse this is the schema, which is the point of reading it here.
        var (db, conn) = NewStore();
        var provider = new ExecFakeProvider(DatabaseProviderType.Postgres, schema: OrdersAndSummary());
        var (svc, connId) = await SetupAsync(db, provider, isReadOnly: false);

        var r = await svc.ExecuteSqlAsync(connId, "UPDATE order_summary SET total = 0");

        Assert.False(r.Success);
        Assert.Equal("policy_denied_view_write", r.ErrorCode);
        Assert.False(provider.WasCalled);
        conn.Dispose();
    }

    [Fact]
    public async Task Reading_a_view_executes()
    {
        var (db, conn) = NewStore();
        var provider = new ExecFakeProvider(DatabaseProviderType.Postgres, schema: OrdersAndSummary());
        var (svc, connId) = await SetupAsync(db, provider, isReadOnly: false);

        var r = await svc.ExecuteSqlAsync(connId, "SELECT id FROM order_summary");

        Assert.True(r.Success);
        conn.Dispose();
    }

    [Fact]
    public async Task An_object_with_no_policy_row_is_writable_on_a_writable_connection()
    {
        var (db, conn) = NewStore();
        var provider = new ExecFakeProvider(DatabaseProviderType.Postgres, schema: OrdersAndSummary());
        var (svc, connId) = await SetupAsync(db, provider, isReadOnly: false);

        var r = await svc.ExecuteSqlAsync(connId, "DELETE FROM orders WHERE id = 1");

        Assert.True(r.Success);
        conn.Dispose();
    }

    [Fact]
    public async Task A_hidden_view_is_refused_as_hidden_rather_than_as_a_view()
    {
        var (db, conn) = NewStore();
        var provider = new ExecFakeProvider(DatabaseProviderType.Postgres, schema: OrdersAndSummary());
        var (svc, connId) = await SetupAsync(db, provider, isReadOnly: false);
        await SetLevelAsync(db, connId, "order_summary", visible: false, write: false);

        var r = await svc.ExecuteSqlAsync(connId, "SELECT id FROM order_summary");

        Assert.False(r.Success);
        Assert.Equal("policy_denied_hidden_table", r.ErrorCode);
        conn.Dispose();
    }

    [Fact]
    public async Task An_unreadable_schema_refuses_the_query_rather_than_guessing()
    {
        // Fail closed: without the schema, a view is indistinguishable from a table, and allowing the
        // query would let a write to a view through unchecked.
        var (db, conn) = NewStore();
        var provider = new ExecFakeProvider(
            DatabaseProviderType.Postgres, schemaFailure: new InvalidOperationException("catalog denied"));
        var (svc, connId) = await SetupAsync(db, provider, isReadOnly: false);

        var r = await svc.ExecuteSqlAsync(connId, "SELECT id FROM orders");

        Assert.False(r.Success);
        Assert.Equal("schema_unavailable", r.ErrorCode);
        Assert.False(provider.WasCalled);
        // The provider's own exception text can echo a connection string, so it must not reach the caller
        // or the audit row.
        Assert.DoesNotContain("catalog denied", r.ErrorMessage ?? "");
        Assert.DoesNotContain("catalog denied", Assert.Single(await AuditAsync(db)).DenyReason ?? "");
        conn.Dispose();
    }

    [Fact]
    public async Task A_connection_with_a_missing_secret_reports_connection_secret_missing_not_schema_unavailable()
    {
        // The secret is deleted directly from the store (real ISecretStore, real DatabaseConnection
        // .ConnectionStringSecretRef) leaving the connection row intact — the same shape ConnectionsPageTests
        // uses for ConnectionTester.TestSavedAsync's null-secret case. A cold cache means the resolver would
        // also need this same secret to read the schema; the secret check must win first so a broken
        // installation is reported as connection_secret_missing (an operational "error"), not
        // schema_unavailable (a policy "deny").
        var (db, conn) = NewStore();
        var secrets = new InMemorySecretStore();
        var connections = new DatabaseConnectionService(db, secrets);
        var created = await connections.CreateAsync(
            new DatabaseConnectionInput("c", DatabaseProviderType.Postgres, false), "conn-string");
        var entity = await db.DatabaseConnections.FindAsync(created.Id);
        await secrets.DeleteAsync(entity!.ConnectionStringSecretRef);

        var registry = new DatabaseProviderRegistry(
            [new ExecFakeProvider(DatabaseProviderType.Postgres, schema: OrdersAndSummary())]);
        var schemas = new SchemaService(connections, registry, db);
        var svc = new QueryExecutionService(
            connections, registry, db, schemas, NullLogger<QueryExecutionService>.Instance);

        var r = await svc.ExecuteSqlAsync(created.Id, "SELECT id FROM orders");

        Assert.False(r.Success);
        Assert.Equal("connection_secret_missing", r.ErrorCode);
        Assert.Equal("error", Assert.Single(await AuditAsync(db)).Decision);
        conn.Dispose();
    }

    [Fact]
    public async Task The_cached_schema_is_reused_rather_than_re_extracted_per_query()
    {
        // SchemaCache exists precisely so the prompt path does not re-read the live catalog per request,
        // and this task put a schema read on the query path too. One extraction, then the cache.
        var (db, conn) = NewStore();
        var provider = new CountingSchemaProvider(DatabaseProviderType.Postgres, OrdersAndSummary());
        var (svc, connId) = await SetupAsync(db, provider, isReadOnly: false);

        await svc.ExecuteSqlAsync(connId, "SELECT id FROM orders");
        await svc.ExecuteSqlAsync(connId, "SELECT id FROM orders");
        await svc.ExecuteSqlAsync(connId, "SELECT id FROM orders");

        Assert.Equal(1, provider.SchemaReads);
        conn.Dispose();
    }

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

    [Theory]
    [InlineData(DatabaseProviderType.Postgres)]
    [InlineData(DatabaseProviderType.SqlServer)]
    public async Task Select_into_on_a_readonly_connection_is_denied_before_execution(DatabaseProviderType type)
    {
        // The statement reads as a SELECT and creates a table, so the read-only flag has to catch it on
        // the strength of the write set alone. It executed before the write set knew about SELECT INTO.
        var (db, conn) = NewStore();
        var provider = new ExecFakeProvider(type);
        var (svc, connId) = await SetupAsync(db, provider, isReadOnly: true);

        var r = await svc.ExecuteSqlAsync(connId, "SELECT * INTO backup FROM orders");

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
        var schemas = new SchemaService(connections, registry, db);
        var svc = new QueryExecutionService(connections, registry, db, schemas, NullLogger<QueryExecutionService>.Instance);

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
