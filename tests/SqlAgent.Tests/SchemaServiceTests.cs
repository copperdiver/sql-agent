using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SqlAgent.Core;
using SqlAgent.Storage;

namespace SqlAgent.Tests;

/// <summary>Returns a canned schema from <see cref="GetSchemaAsync"/>; no real database I/O.</summary>
file sealed class SchemaFakeProvider(DatabaseProviderType type, DatabaseSchema schema) : IDatabaseProvider
{
    public DatabaseProviderType ProviderType => type;

    public Task<ConnectionTestResult> TestConnectionAsync(string connectionString, CancellationToken ct = default)
        => Task.FromResult(ConnectionTestResult.Ok(null, 0));

    public Task<DatabaseSchema> GetSchemaAsync(string connectionString, CancellationToken ct = default)
        => Task.FromResult(schema);

    public Task<QueryResultSet> ExecuteQueryAsync(
        string connectionString, string sql, QueryExecutionOptions options, CancellationToken ct = default)
        => throw new NotSupportedException();
}

/// <summary>Counts <see cref="GetSchemaAsync"/> calls so a test can prove the cache is reused (extract-once).</summary>
file sealed class CountingSchemaProvider(DatabaseProviderType type, DatabaseSchema schema) : IDatabaseProvider
{
    public int SchemaCalls { get; private set; }
    public DatabaseProviderType ProviderType => type;

    public Task<ConnectionTestResult> TestConnectionAsync(string connectionString, CancellationToken ct = default)
        => Task.FromResult(ConnectionTestResult.Ok(null, 0));

    public Task<DatabaseSchema> GetSchemaAsync(string connectionString, CancellationToken ct = default)
    {
        SchemaCalls++;
        return Task.FromResult(schema);
    }

    public Task<QueryResultSet> ExecuteQueryAsync(
        string connectionString, string sql, QueryExecutionOptions options, CancellationToken ct = default)
        => throw new NotSupportedException();
}

public class SchemaServiceTests
{
    private static readonly DatabaseSchema Sample = SchemaModel.Build(
        columns:
        [
            ("dbo", "Orders", "Id", "int", false, null, 10, 0),
            ("dbo", "Secret", "Id", "int", false, null, 10, 0),
        ],
        primaryKeys: [],
        foreignKeys:
        [
            ("dbo", "Orders", "SecretId", "dbo", "Secret", "Id"),
        ]);

    private static (SqlAgentDbContext db, DatabaseConnectionService svc, SqliteConnection conn) NewDb()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        var db = new SqlAgentDbContext(
            new DbContextOptionsBuilder<SqlAgentDbContext>().UseSqlite(conn).Options);
        db.Database.EnsureCreated();
        return (db, new DatabaseConnectionService(db, new InMemorySecretStore()), conn);
    }

    [Fact]
    public async Task GetVisibleSchema_omits_tables_marked_invisible_and_their_inbound_fk()
    {
        var (db, svc, conn) = NewDb();
        var created = await svc.CreateAsync(
            new DatabaseConnectionInput("c", DatabaseProviderType.SqlServer, false), "Server=x");
        db.TablePolicies.Add(new TablePolicy
        {
            Id = Guid.NewGuid(), DatabaseConnectionId = created.Id,
            SchemaName = "dbo", TableName = "Secret", IsVisible = false,
        });
        await db.SaveChangesAsync();

        var providers = new DatabaseProviderRegistry(
            [new SchemaFakeProvider(DatabaseProviderType.SqlServer, Sample)]);
        var schema = await new SchemaService(svc, providers, db).GetVisibleSchemaAsync(created.Id);

        Assert.NotNull(schema);
        var table = Assert.Single(schema!.Tables);
        Assert.Equal("Orders", table.Name);
        Assert.Empty(table.ForeignKeys); // FK to hidden Secret dropped
        conn.Dispose();
    }

    [Fact]
    public async Task GetVisibleSchema_returns_all_tables_when_no_policy_hides_them()
    {
        var (db, svc, conn) = NewDb();
        var created = await svc.CreateAsync(
            new DatabaseConnectionInput("c", DatabaseProviderType.SqlServer, false), "Server=x");

        var providers = new DatabaseProviderRegistry(
            [new SchemaFakeProvider(DatabaseProviderType.SqlServer, Sample)]);
        var schema = await new SchemaService(svc, providers, db).GetVisibleSchemaAsync(created.Id);

        Assert.NotNull(schema);
        Assert.Equal(2, schema!.Tables.Count);
        conn.Dispose();
    }

    [Fact]
    public async Task GetVisibleSchema_returns_null_for_an_unknown_connection()
    {
        var (db, svc, conn) = NewDb();
        var providers = new DatabaseProviderRegistry(
            [new SchemaFakeProvider(DatabaseProviderType.SqlServer, Sample)]);

        Assert.Null(await new SchemaService(svc, providers, db).GetVisibleSchemaAsync(Guid.NewGuid()));
        conn.Dispose();
    }

    [Fact]
    public async Task Refresh_stores_filtered_schema_json_excluding_hidden_tables()
    {
        var (db, svc, conn) = NewDb();
        var created = await svc.CreateAsync(
            new DatabaseConnectionInput("c", DatabaseProviderType.SqlServer, false), "Server=x");
        db.TablePolicies.Add(new TablePolicy
        {
            Id = Guid.NewGuid(), DatabaseConnectionId = created.Id,
            SchemaName = "dbo", TableName = "Secret", IsVisible = false,
        });
        await db.SaveChangesAsync();

        var providers = new DatabaseProviderRegistry(
            [new SchemaFakeProvider(DatabaseProviderType.SqlServer, Sample)]);
        await new SchemaService(svc, providers, db).RefreshAsync(created.Id);

        var cache = await db.SchemaCaches.SingleAsync(c => c.DatabaseConnectionId == created.Id);
        Assert.NotEqual("", cache.SchemaHash);
        Assert.Contains("Orders", cache.FilteredSchemaJson);
        Assert.DoesNotContain("Secret", cache.FilteredSchemaJson); // hidden table never reaches the cache
        Assert.DoesNotContain("\n", cache.FilteredSchemaJson);     // compact: minified, one line
        conn.Dispose();
    }

    [Fact]
    public async Task GetOrRefresh_populates_on_first_load_then_reuses_the_cache()
    {
        var (db, svc, conn) = NewDb();
        var created = await svc.CreateAsync(
            new DatabaseConnectionInput("c", DatabaseProviderType.SqlServer, false), "Server=x");
        var provider = new CountingSchemaProvider(DatabaseProviderType.SqlServer, Sample);
        var providers = new DatabaseProviderRegistry([provider]);
        var schemas = new SchemaService(svc, providers, db);

        var first = await schemas.GetOrRefreshAsync(created.Id);   // first load -> live extract + cache
        var second = await schemas.GetOrRefreshAsync(created.Id);  // reuse -> no second extraction

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(2, second!.Tables.Count);
        Assert.Equal(1, provider.SchemaCalls); // extracted exactly once
        conn.Dispose();
    }

    [Fact]
    public async Task Setting_a_table_hidden_invalidates_the_cache_so_it_cannot_leak()
    {
        var (db, svc, conn) = NewDb();
        var created = await svc.CreateAsync(
            new DatabaseConnectionInput("c", DatabaseProviderType.SqlServer, false), "Server=x");
        var providers = new DatabaseProviderRegistry(
            [new SchemaFakeProvider(DatabaseProviderType.SqlServer, Sample)]);
        var schemas = new SchemaService(svc, providers, db);
        var policies = new TablePolicyService(svc, providers, db);

        await schemas.GetOrRefreshAsync(created.Id); // cache both tables
        Assert.True(await db.SchemaCaches.AnyAsync(c => c.DatabaseConnectionId == created.Id));

        await policies.SetVisibilityAsync(created.Id, "dbo", "Secret", isVisible: false);
        Assert.False(await db.SchemaCaches.AnyAsync(c => c.DatabaseConnectionId == created.Id)); // invalidated

        var after = await schemas.GetOrRefreshAsync(created.Id); // re-extract under new policy
        Assert.Equal("Orders", Assert.Single(after!.Tables).Name);
        conn.Dispose();
    }
}
