using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SqlAgent.Core;
using SqlAgent.Core.Policy;
using SqlAgent.Storage;

namespace SqlAgent.Tests;

/// <summary>
/// Provider double returning a fixed schema with one table and one view, so the service's two-dimension
/// mapping can be exercised without a live database.
/// </summary>
file sealed class SchemaFakeProvider(DatabaseProviderType type, DatabaseSchema schema) : IDatabaseProvider
{
    public DatabaseProviderType ProviderType => type;

    public Task<ConnectionTestResult> TestConnectionAsync(string cs, CancellationToken ct = default)
        => Task.FromResult(ConnectionTestResult.Ok(null, 0));

    public Task<DatabaseSchema> GetSchemaAsync(string cs, CancellationToken ct = default)
        => Task.FromResult(schema);

    public Task<QueryResultSet> ExecuteQueryAsync(
        string cs, string sql, QueryExecutionOptions options, CancellationToken ct = default)
        => throw new NotSupportedException();
}

public class TablePolicyServiceTests : IDisposable
{
    private readonly SqliteConnection _conn = new("DataSource=:memory:");
    private readonly SqlAgentDbContext _db;

    public TablePolicyServiceTests()
    {
        _conn.Open();
        _db = new SqlAgentDbContext(
            new DbContextOptionsBuilder<SqlAgentDbContext>().UseSqlite(_conn).Options);
        _db.Database.EnsureCreated();
    }

    public void Dispose() { _db.Dispose(); _conn.Dispose(); }

    private static DatabaseSchema Fixture() => new(
        [new SchemaTable("dbo", "orders", [new SchemaColumn("id", "int", false)], ["id"], [], []),
         new SchemaTable("sales", "leads", [new SchemaColumn("id", "int", false)], [], [], [])],
        [new SchemaView("dbo", "order_summary", [new SchemaColumn("id", "int", false)])]);

    private async Task<(TablePolicyService svc, Guid id)> SetupAsync()
    {
        var connections = new DatabaseConnectionService(_db, new InMemorySecretStore());
        var created = await connections.CreateAsync(
            new DatabaseConnectionInput("c", DatabaseProviderType.Postgres, false), "conn-string");
        var registry = new DatabaseProviderRegistry(
            [new SchemaFakeProvider(DatabaseProviderType.Postgres, Fixture())]);
        return (new TablePolicyService(connections, registry, _db), created.Id);
    }

    private async Task SeedCacheAsync(Guid connectionId) =>
        await _db.SchemaCaches.AddAsync(new SchemaCache
        {
            Id = Guid.NewGuid(),
            DatabaseConnectionId = connectionId,
            SchemaHash = "h",
            FilteredSchemaJson = "{}",
            GeneratedAt = DateTime.UtcNow,
        });

    [Fact]
    public async Task Objects_with_no_policy_row_are_listed_as_fully_accessible()
    {
        // The phase's central decision, at the layer the page reads.
        var (svc, id) = await SetupAsync();

        var objects = await svc.ListObjectsAsync(id);

        Assert.NotNull(objects);
        Assert.All(objects, o => Assert.Equal(ObjectAccess.Full, o.Access));
    }

    [Fact]
    public async Task Tables_and_views_are_listed_together_and_labelled()
    {
        var (svc, id) = await SetupAsync();

        var objects = await svc.ListObjectsAsync(id);

        Assert.Equal(3, objects!.Count);
        Assert.Equal(DatabaseObjectKind.View,
            objects.Single(o => o.Name == "order_summary").Kind);
        Assert.All(objects.Where(o => o.Name != "order_summary"),
            o => Assert.Equal(DatabaseObjectKind.Table, o.Kind));
        Assert.Equal("sales", objects.Single(o => o.Name == "leads").Schema);
    }

    [Theory]
    [InlineData(ObjectAccess.Hidden, false, false)]
    [InlineData(ObjectAccess.ReadOnly, true, false)]
    [InlineData(ObjectAccess.Full, true, true)]
    public async Task Each_level_writes_the_documented_columns(
        ObjectAccess access, bool expectedVisible, bool expectedWrite)
    {
        var (svc, id) = await SetupAsync();

        var outcome = await svc.SetAccessAsync(id, "dbo", "orders", DatabaseObjectKind.Table, access);

        Assert.Equal(SetAccessOutcome.Applied, outcome);
        var row = await _db.TablePolicies.SingleAsync();
        Assert.Equal(expectedVisible, row.IsVisible);
        Assert.Equal(expectedWrite, row.CanWrite);
        // CanRead never varies — the level table produces no state where it is false. It is written true
        // so a reader of the store cannot mistake a stale false for a decision somebody made.
        Assert.True(row.CanRead);
    }

    [Fact]
    public async Task A_level_round_trips_through_the_list()
    {
        var (svc, id) = await SetupAsync();

        await svc.SetAccessAsync(id, "dbo", "orders", DatabaseObjectKind.Table, ObjectAccess.ReadOnly);
        var objects = await svc.ListObjectsAsync(id);

        Assert.Equal(ObjectAccess.ReadOnly, objects!.Single(o => o.Name == "orders").Access);
        Assert.Equal(ObjectAccess.Full, objects.Single(o => o.Name == "leads").Access);
    }

    [Fact]
    public async Task Setting_the_same_object_twice_updates_the_one_row()
    {
        // The unique index on (connection, schema, name) makes a second insert a crash rather than a
        // duplicate, so the upsert has to find the existing row.
        var (svc, id) = await SetupAsync();

        await svc.SetAccessAsync(id, "dbo", "orders", DatabaseObjectKind.Table, ObjectAccess.Hidden);
        await svc.SetAccessAsync(id, "dbo", "orders", DatabaseObjectKind.Table, ObjectAccess.Full);

        var row = Assert.Single(await _db.TablePolicies.ToListAsync());
        Assert.True(row.IsVisible);
        Assert.True(row.CanWrite);
    }

    [Fact]
    public async Task A_view_cannot_be_made_writable()
    {
        // The panel never offers the level, so this refusal exists to keep the service correct
        // independently of its caller.
        var (svc, id) = await SetupAsync();

        var outcome = await svc.SetAccessAsync(
            id, "dbo", "order_summary", DatabaseObjectKind.View, ObjectAccess.Full);

        Assert.Equal(SetAccessOutcome.ViewCannotBeWritable, outcome);
        Assert.Empty(await _db.TablePolicies.ToListAsync());
    }

    [Theory]
    [InlineData(ObjectAccess.Hidden)]
    [InlineData(ObjectAccess.ReadOnly)]
    public async Task A_view_accepts_the_two_levels_it_is_allowed(ObjectAccess access)
    {
        var (svc, id) = await SetupAsync();

        var outcome = await svc.SetAccessAsync(
            id, "dbo", "order_summary", DatabaseObjectKind.View, access);

        Assert.Equal(SetAccessOutcome.Applied, outcome);
        Assert.Equal(access, (await svc.ListObjectsAsync(id))!.Single(o => o.Name == "order_summary").Access);
    }

    [Fact]
    public async Task Setting_a_whole_schema_sets_every_object_under_it_and_nothing_else()
    {
        var (svc, id) = await SetupAsync();

        var outcome = await svc.SetSchemaAccessAsync(id, "dbo", ObjectAccess.Hidden);

        Assert.Equal(SetAccessOutcome.Applied, outcome);
        var objects = await svc.ListObjectsAsync(id);
        Assert.Equal(ObjectAccess.Hidden, objects!.Single(o => o.Name == "orders").Access);
        Assert.Equal(ObjectAccess.Hidden, objects.Single(o => o.Name == "order_summary").Access);
        Assert.Equal(ObjectAccess.Full, objects.Single(o => o.Name == "leads").Access);
    }

    [Fact]
    public async Task Setting_a_whole_schema_to_full_clamps_its_views_to_read_only()
    {
        // "Everything under this header, as far as each object is allowed to go" — failing the batch
        // because one member is a view would make the header useless on any schema that has one.
        var (svc, id) = await SetupAsync();

        var outcome = await svc.SetSchemaAccessAsync(id, "dbo", ObjectAccess.Full);

        Assert.Equal(SetAccessOutcome.Applied, outcome);
        var objects = await svc.ListObjectsAsync(id);
        Assert.Equal(ObjectAccess.Full, objects!.Single(o => o.Name == "orders").Access);
        Assert.Equal(ObjectAccess.ReadOnly, objects.Single(o => o.Name == "order_summary").Access);
    }

    [Fact]
    public async Task Setting_one_object_invalidates_the_cached_schema()
    {
        var (svc, id) = await SetupAsync();
        await SeedCacheAsync(id);
        await _db.SaveChangesAsync();

        await svc.SetAccessAsync(id, "dbo", "orders", DatabaseObjectKind.Table, ObjectAccess.Hidden);

        Assert.Empty(await _db.SchemaCaches.ToListAsync());
    }

    [Fact]
    public async Task Setting_a_whole_schema_invalidates_the_cached_schema()
    {
        // Asserted separately from the single-object path: the batch is a second write path, and a cache
        // left behind by it is a hidden object still reaching the model.
        var (svc, id) = await SetupAsync();
        await SeedCacheAsync(id);
        await _db.SaveChangesAsync();

        await svc.SetSchemaAccessAsync(id, "dbo", ObjectAccess.Hidden);

        Assert.Empty(await _db.SchemaCaches.ToListAsync());
    }

    [Fact]
    public async Task A_missing_connection_reports_itself_rather_than_throwing()
    {
        var (svc, _) = await SetupAsync();

        Assert.Null(await svc.ListObjectsAsync(Guid.NewGuid()));
        Assert.Equal(SetAccessOutcome.ConnectionMissing,
            await svc.SetAccessAsync(Guid.NewGuid(), "dbo", "orders", DatabaseObjectKind.Table, ObjectAccess.Full));
        Assert.Equal(SetAccessOutcome.ConnectionMissing,
            await svc.SetSchemaAccessAsync(Guid.NewGuid(), "dbo", ObjectAccess.Full));
    }
}
