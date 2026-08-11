using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SqlAgent.Api.Mcp;
using SqlAgent.Core;
using SqlAgent.Storage;

namespace SqlAgent.Tests;

/// <summary>
/// Provider double for the MCP tool layer: returns a canned schema and result set so describe_schema /
/// query_database behavior is exercised without a live database server.
/// </summary>
file sealed class ToolFakeProvider(DatabaseSchema? schema = null, QueryResultSet? result = null) : IDatabaseProvider
{
    public DatabaseProviderType ProviderType => DatabaseProviderType.Postgres;

    public Task<ConnectionTestResult> TestConnectionAsync(string connectionString, CancellationToken ct = default)
        => Task.FromResult(ConnectionTestResult.Ok(null, 0));

    public Task<DatabaseSchema> GetSchemaAsync(string connectionString, CancellationToken ct = default)
        => Task.FromResult(schema ?? new DatabaseSchema([]));

    public Task<QueryResultSet> ExecuteQueryAsync(
        string connectionString, string sql, QueryExecutionOptions options, CancellationToken ct = default)
        => Task.FromResult(result ?? new QueryResultSet([], [], false));
}

public class McpToolServiceTests
{
    private static (SqlAgentDbContext db, SqliteConnection conn) NewStore()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        var db = new SqlAgentDbContext(new DbContextOptionsBuilder<SqlAgentDbContext>().UseSqlite(conn).Options);
        db.Database.EnsureCreated();
        return (db, conn);
    }

    private static (McpToolService tools, DatabaseConnectionService connections, SqlAgentDbContext db) Build(
        SqlAgentDbContext db, IDatabaseProvider provider, ISecretStore? secrets = null, string? presentedToken = null)
    {
        secrets ??= new InMemorySecretStore();
        var connections = new DatabaseConnectionService(db, secrets);
        var registry = new DatabaseProviderRegistry([provider]);
        var schemas = new SchemaService(connections, registry, db);
        var executor = new QueryExecutionService(connections, registry, db);
        var authenticator = new LocalTokenAuthenticator(secrets, NullLogger<LocalTokenAuthenticator>.Instance);
        return (new McpToolService(connections, schemas, executor, authenticator, new McpClientToken(presentedToken)), connections, db);
    }

    [Fact]
    public async Task ListDatabases_returns_configured_connections()
    {
        var (db, conn) = NewStore();
        var (tools, connections, _) = Build(db, new ToolFakeProvider());
        await connections.CreateAsync(new DatabaseConnectionInput("analytics", DatabaseProviderType.Postgres, IsReadOnly: true), "cs");
        await connections.CreateAsync(new DatabaseConnectionInput("ops", DatabaseProviderType.SqlServer, IsReadOnly: false), "cs");

        var r = await tools.ListDatabasesAsync();

        Assert.True(r.Ok);
        Assert.Equal(2, r.Databases.Count);
        var analytics = r.Databases.Single(d => d.Name == "analytics");
        Assert.Equal("postgres", analytics.Provider);
        Assert.True(analytics.ReadOnly);
        Assert.True(Guid.TryParse(analytics.Id, out _));
        Assert.Equal("sqlserver", r.Databases.Single(d => d.Name == "ops").Provider);

        conn.Dispose();
    }

    [Fact]
    public async Task DescribeSchema_invalid_id_returns_invalid_database_id()
    {
        var (db, conn) = NewStore();
        var (tools, _, _) = Build(db, new ToolFakeProvider());

        var r = await tools.DescribeSchemaAsync("not-a-guid");

        Assert.False(r.Ok);
        Assert.Equal("invalid_database_id", r.ErrorCode);
        conn.Dispose();
    }

    [Fact]
    public async Task DescribeSchema_unknown_connection_returns_connection_not_found()
    {
        var (db, conn) = NewStore();
        var (tools, _, _) = Build(db, new ToolFakeProvider());

        var r = await tools.DescribeSchemaAsync(Guid.NewGuid().ToString());

        Assert.False(r.Ok);
        Assert.Equal("connection_not_found", r.ErrorCode);
        conn.Dispose();
    }

    [Fact]
    public async Task DescribeSchema_returns_visible_tables_and_omits_hidden()
    {
        var (db, conn) = NewStore();
        var schema = new DatabaseSchema([
            new SchemaTable("public", "orders",
                [new SchemaColumn("id", "int", false), new SchemaColumn("total", "numeric", true)],
                ["id"], [], []),
            new SchemaTable("public", "secrets",
                [new SchemaColumn("token", "text", false)], [], [], []),
        ]);
        var (tools, connections, _) = Build(db, new ToolFakeProvider(schema));
        var created = await connections.CreateAsync(
            new DatabaseConnectionInput("c", DatabaseProviderType.Postgres, IsReadOnly: true), "cs");
        db.TablePolicies.Add(new TablePolicy
        {
            Id = Guid.NewGuid(),
            DatabaseConnectionId = created.Id,
            SchemaName = "public",
            TableName = "secrets",
            IsVisible = false,
        });
        await db.SaveChangesAsync();

        var r = await tools.DescribeSchemaAsync(created.Id.ToString());

        Assert.True(r.Ok);
        Assert.Equal(created.Id.ToString(), r.DatabaseId);
        var table = Assert.Single(r.Tables!);            // secrets is hidden, only orders remains
        Assert.Equal("orders", table.Name);
        Assert.Equal(["id", "total"], table.Columns.Select(c => c.Name));
        Assert.Equal(["id"], table.PrimaryKey);
        conn.Dispose();
    }

    [Fact]
    public async Task QueryDatabase_invalid_id_returns_invalid_database_id()
    {
        var (db, conn) = NewStore();
        var (tools, _, _) = Build(db, new ToolFakeProvider());

        var r = await tools.QueryDatabaseAsync("nope", "SELECT 1");

        Assert.False(r.Ok);
        Assert.Equal("invalid_database_id", r.ErrorCode);
        conn.Dispose();
    }

    [Fact]
    public async Task QueryDatabase_success_returns_rows_and_metadata()
    {
        var (db, conn) = NewStore();
        var result = new QueryResultSet(["id", "name"], [new object?[] { 1, "a" }], Truncated: false);
        var (tools, connections, _) = Build(db, new ToolFakeProvider(result: result));
        var created = await connections.CreateAsync(
            new DatabaseConnectionInput("c", DatabaseProviderType.Postgres, IsReadOnly: true), "cs");

        var r = await tools.QueryDatabaseAsync(created.Id.ToString(), "SELECT id, name FROM orders");

        Assert.True(r.Ok);
        Assert.Null(r.ErrorCode);
        Assert.Equal(["id", "name"], r.Columns);
        Assert.Equal(1, r.RowCount);
        Assert.False(r.Truncated);
        conn.Dispose();
    }

    [Fact]
    public async Task QueryDatabase_write_on_readonly_is_denied_with_stable_code()
    {
        var (db, conn) = NewStore();
        var (tools, connections, _) = Build(db, new ToolFakeProvider());
        var created = await connections.CreateAsync(
            new DatabaseConnectionInput("c", DatabaseProviderType.Postgres, IsReadOnly: true), "cs");

        var r = await tools.QueryDatabaseAsync(created.Id.ToString(), "UPDATE orders SET total = 0");

        Assert.False(r.Ok);
        Assert.Equal("policy_denied_readonly", r.ErrorCode);
        conn.Dispose();
    }

    [Fact]
    public async Task QueryDatabase_hidden_table_is_denied_with_stable_code()
    {
        var (db, conn) = NewStore();
        var (tools, connections, _) = Build(db, new ToolFakeProvider());
        var created = await connections.CreateAsync(
            new DatabaseConnectionInput("c", DatabaseProviderType.Postgres, IsReadOnly: true), "cs");
        db.TablePolicies.Add(new TablePolicy
        {
            Id = Guid.NewGuid(),
            DatabaseConnectionId = created.Id,
            SchemaName = "public",
            TableName = "secrets",
            IsVisible = false,
        });
        await db.SaveChangesAsync();

        var r = await tools.QueryDatabaseAsync(created.Id.ToString(), "SELECT * FROM secrets");

        Assert.False(r.Ok);
        Assert.Equal("policy_denied_hidden_table", r.ErrorCode);
        conn.Dispose();
    }

    // ---- CD-51 Story 1.7: local-access token enforcement ----

    private static async Task<SqlAgentDbContext> StoreWithToken(SqliteConnection conn, ISecretStore secrets, string token)
    {
        var db = new SqlAgentDbContext(new DbContextOptionsBuilder<SqlAgentDbContext>().UseSqlite(conn).Options);
        db.Database.EnsureCreated();
        await secrets.SetAsync(LocalTokenAuthenticator.TokenSecretReference, token);
        return db;
    }

    [Fact]
    public async Task Tools_run_when_no_token_is_configured()
    {
        var (db, conn) = NewStore();
        var (tools, _, _) = Build(db, new ToolFakeProvider()); // no token seeded, none presented
        var r = await tools.ListDatabasesAsync();
        Assert.True(r.Ok);
        conn.Dispose();
    }

    [Fact]
    public async Task Tools_run_with_a_valid_token()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        var secrets = new InMemorySecretStore();
        var db = await StoreWithToken(conn, secrets, "s3cret");
        var (tools, _, _) = Build(db, new ToolFakeProvider(), secrets, presentedToken: "s3cret");

        var r = await tools.ListDatabasesAsync();

        Assert.True(r.Ok);
        Assert.Null(r.ErrorCode);
        conn.Dispose();
    }

    [Fact]
    public async Task Tools_reject_a_missing_token()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        var secrets = new InMemorySecretStore();
        var db = await StoreWithToken(conn, secrets, "s3cret");
        var (tools, _, _) = Build(db, new ToolFakeProvider(), secrets, presentedToken: null);

        var list = await tools.ListDatabasesAsync();
        var schema = await tools.DescribeSchemaAsync(Guid.NewGuid().ToString());
        var query = await tools.QueryDatabaseAsync(Guid.NewGuid().ToString(), "SELECT 1");

        Assert.False(list.Ok);
        Assert.Equal("unauthorized", list.ErrorCode);
        Assert.Equal("unauthorized", schema.ErrorCode);  // gate runs before id parsing
        Assert.Equal("unauthorized", query.ErrorCode);
        conn.Dispose();
    }

    [Fact]
    public async Task Tools_reject_an_invalid_token()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        var secrets = new InMemorySecretStore();
        var db = await StoreWithToken(conn, secrets, "s3cret");
        var (tools, _, _) = Build(db, new ToolFakeProvider(), secrets, presentedToken: "wrong");

        var r = await tools.ListDatabasesAsync();

        Assert.False(r.Ok);
        Assert.Equal("unauthorized", r.ErrorCode);
        conn.Dispose();
    }
}
