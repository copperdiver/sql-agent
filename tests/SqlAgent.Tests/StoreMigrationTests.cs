using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using SqlAgent.Core;
using SqlAgent.Storage;

namespace SqlAgent.Tests;

/// <summary>
/// The riskiest change in Phase B1. Program.cs called EnsureCreatedAsync, which never alters a store
/// that already exists — so every store in the field has the six original tables and no
/// __EFMigrationsHistory. Running MigrateAsync against one of those tries to CREATE TABLE over tables
/// that are already there and throws, taking the host down on startup with the user's data intact but
/// unreachable. These tests are the only thing standing between that and a shipped release.
/// </summary>
public class StoreMigrationTests : IDisposable
{
    // A real file, not :memory:, because the shim reads sqlite_master through a second command and the
    // point of the exercise is a store that outlives a connection.
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"sqlagent-migr-{Guid.NewGuid():N}");

    private string DbPath => Path.Combine(_dir, "sqlagent.db");
    private string ConnectionString => $"Data Source={DbPath}";

    public StoreMigrationTests() => Directory.CreateDirectory(_dir);

    private SqlAgentDbContext NewContext() => new(
        new DbContextOptionsBuilder<SqlAgentDbContext>().UseSqlite(ConnectionString).Options);

    [Fact]
    public async Task A_store_created_by_EnsureCreated_migrates_and_keeps_its_data()
    {
        // The legacy store is built through LegacyStoreDbContext (below), which declares exactly the six
        // pre-B1 entities. Calling EnsureCreated on today's context instead would create the chat tables
        // too and the shim would never be exercised — the test would pass against a store shaped
        // nothing like the ones this code exists to rescue.
        Guid connectionId;
        await using (var legacy = NewLegacyContext())
        {
            await legacy.Database.EnsureCreatedAsync();
            var row = new DatabaseConnection
            {
                Id = Guid.NewGuid(),
                Name = "prod",
                ProviderType = DatabaseProviderType.Postgres,
                ConnectionStringSecretRef = "db:abc",
                IsReadOnly = true,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            legacy.Set<DatabaseConnection>().Add(row);
            await legacy.SaveChangesAsync();
            connectionId = row.Id;
        }
        SqliteConnection.ClearAllPools();

        await using var db = NewContext();
        await StoreInitializer.InitializeAsync(db, NullLogger.Instance);

        // The user's row is still there...
        var kept = await db.DatabaseConnections.SingleAsync();
        Assert.Equal(connectionId, kept.Id);
        Assert.Equal("prod", kept.Name);
        // ...and the store now knows it is migrated, so the next start is an ordinary no-op migration.
        Assert.NotEmpty(await db.Database.GetAppliedMigrationsAsync());
    }

    [Fact]
    public async Task An_empty_store_migrates_from_nothing()
    {
        await using var db = NewContext();

        await StoreInitializer.InitializeAsync(db, NullLogger.Instance);

        Assert.NotEmpty(await db.Database.GetAppliedMigrationsAsync());
        Assert.Empty(await db.DatabaseConnections.ToListAsync());
    }

    [Fact]
    public async Task Initializing_twice_is_a_no_op_the_second_time()
    {
        // Every host start runs this. The second run must not try to stamp the baseline again — the
        // insert would violate the history table's primary key and stop a host whose store is fine.
        // Starting from a legacy store (not an empty one) is what actually exercises the stamp path:
        // initializing an empty store never stamps at all, so it would never approach the guard this
        // test means to verify.
        await using (var legacy = NewLegacyContext())
            await legacy.Database.EnsureCreatedAsync();
        SqliteConnection.ClearAllPools();

        await using var db = NewContext();
        await StoreInitializer.InitializeAsync(db, NullLogger.Instance);

        await StoreInitializer.InitializeAsync(db, NullLogger.Instance);

        Assert.NotEmpty(await db.Database.GetAppliedMigrationsAsync());
    }

    [Fact]
    public async Task A_history_table_left_empty_by_an_interrupted_stamp_is_treated_as_unmigrated()
    {
        // Reproduces exactly what a process death between StoreInitializer's create-history-table and
        // insert-baseline-row statements leaves behind: the history table exists, but has no row. A
        // version of the detection that only checked "does the history table exist" would read that as
        // "already migrated", skip straight to MigrateAsync, and hit "table DatabaseConnections already
        // exists" — permanently, since every later start reaches the same wrong conclusion from the same
        // empty table.
        Guid connectionId;
        await using (var legacy = NewLegacyContext())
        {
            await legacy.Database.EnsureCreatedAsync();
            var row = new DatabaseConnection
            {
                Id = Guid.NewGuid(),
                Name = "prod",
                ProviderType = DatabaseProviderType.Postgres,
                ConnectionStringSecretRef = "db:abc",
                IsReadOnly = true,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            legacy.Set<DatabaseConnection>().Add(row);
            await legacy.SaveChangesAsync();
            connectionId = row.Id;
        }
        SqliteConnection.ClearAllPools();

        // Create the history table without inserting the baseline row — the half-stamped state.
        await using (var setup = NewContext())
        {
            var history = setup.GetService<IHistoryRepository>();
            await setup.Database.ExecuteSqlRawAsync(history.GetCreateIfNotExistsScript());
        }
        SqliteConnection.ClearAllPools();

        await using var db = NewContext();
        await StoreInitializer.InitializeAsync(db, NullLogger.Instance);

        var kept = await db.DatabaseConnections.SingleAsync();
        Assert.Equal(connectionId, kept.Id);
        Assert.NotEmpty(await db.Database.GetAppliedMigrationsAsync());
    }

    [Fact]
    public async Task A_rescued_legacy_store_also_accepts_the_next_migration()
    {
        // The baseline shim declares InitialCreate applied without ever running it — it trusts that the
        // legacy tables it found are shaped exactly like InitialCreate would have made them. Task 2's
        // ChatPersistence migration is the first real test of that trust: if the shim's stamp and
        // ChatPersistence's assumptions about the prior schema ever disagree, this is where it would
        // show up, not in a test that only exercises the shim in isolation.
        Guid connectionId;
        await using (var legacy = NewLegacyContext())
        {
            await legacy.Database.EnsureCreatedAsync();
            var row = new DatabaseConnection
            {
                Id = Guid.NewGuid(),
                Name = "prod",
                ProviderType = DatabaseProviderType.Postgres,
                ConnectionStringSecretRef = "db:abc",
                IsReadOnly = true,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            legacy.Set<DatabaseConnection>().Add(row);
            await legacy.SaveChangesAsync();
            connectionId = row.Id;
        }
        SqliteConnection.ClearAllPools();

        await using var db = NewContext();
        await StoreInitializer.InitializeAsync(db, NullLogger.Instance);

        var tables = await db.Database
            .SqlQueryRaw<string>("SELECT name AS Value FROM sqlite_master WHERE type = 'table'")
            .ToListAsync();
        Assert.Contains("Chats", tables);
        Assert.Contains("ChatMessages", tables);
        Assert.Contains("ChatMessageDatabases", tables);
        // States the intent directly rather than through the tables it happened to create: this survives
        // a change to what ChatPersistence creates, and would fail if the rescue path ever accepted the
        // migration's tables without actually recording it as applied.
        Assert.Contains("20260812223903_ChatPersistence", await db.Database.GetAppliedMigrationsAsync());

        var kept = await db.DatabaseConnections.SingleAsync();
        Assert.Equal(connectionId, kept.Id);
        Assert.Equal("prod", kept.Name);
    }

    private LegacyStoreDbContext NewLegacyContext() => new(
        new DbContextOptionsBuilder<LegacyStoreDbContext>().UseSqlite(ConnectionString).Options);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}

/// <summary>
/// The store as it was before Phase B1: the same six entity classes (they are unchanged, so the types
/// are reused rather than copied), with the same keys and indexes SqlAgentDbContext declared for them
/// at the time. It exists only so a test can produce a genuinely legacy-shaped SQLite file through
/// EnsureCreated, which is how every store in the field was made.
/// </summary>
public sealed class LegacyStoreDbContext(DbContextOptions<LegacyStoreDbContext> options) : DbContext(options)
{
    // Deliberately named to match SqlAgentDbContext's own DbSet properties: without an exposed DbSet, EF
    // Core's default table-naming convention falls back to the bare (singular) entity class name, which
    // would give this store a schema that shares no table names with the real one — MigrateAsync would
    // then create a second, empty set of tables alongside these instead of recognizing them as legacy.
    public DbSet<DatabaseConnection> DatabaseConnections => Set<DatabaseConnection>();
    public DbSet<TablePolicy> TablePolicies => Set<TablePolicy>();
    public DbSet<SchemaCache> SchemaCaches => Set<SchemaCache>();
    public DbSet<QueryAuditLog> QueryAuditLogs => Set<QueryAuditLog>();
    public DbSet<AppSetting> AppSettings => Set<AppSetting>();
    public DbSet<Secret> Secrets => Set<Secret>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<DatabaseConnection>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Name).IsUnique();
        });
        b.Entity<TablePolicy>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.DatabaseConnectionId, x.SchemaName, x.TableName }).IsUnique();
        });
        b.Entity<SchemaCache>().HasKey(x => x.Id);
        b.Entity<QueryAuditLog>().HasKey(x => x.Id);
        b.Entity<AppSetting>().HasKey(x => x.Key);
        b.Entity<Secret>().HasKey(x => x.Reference);
    }
}
