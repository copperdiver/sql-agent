using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging;
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

    [Fact]
    public async Task A_chat_and_its_message_survive_the_migration_that_rebuilds_the_Chats_table()
    {
        // Phase B2's Projects migration adds a foreign key from Chats to the new Projects table. SQLite
        // has no ALTER TABLE ADD CONSTRAINT, so EF's migration generator rebuilds the whole Chats table to
        // add it: create a new table, copy every row across, drop the old one, rename the new one into
        // place. ChatMessages cascades to Chats, so this is the one path in the phase that touches a real
        // user's conversations rather than just their connections — every other StoreMigrationTests test
        // above seeds only a DatabaseConnection. Seeds a chat and a message the way a store that already
        // went through Phase B1 would have them — via the real migrator, stopping at ChatPersistence, with
        // proper history rows for it, not through the six-table LegacyStoreDbContext above, which predates
        // chats entirely — then lets StoreInitializer apply the rest and checks both survive.
        Guid chatId = Guid.NewGuid(), messageId = Guid.NewGuid();
        await using (var db = NewContext())
        {
            await db.GetService<IMigrator>().MigrateAsync("20260812223903_ChatPersistence");

            // SqlAgentDbContext's own model already declares Chat.ProjectId, which does not exist as a
            // column yet at this point — inserting through it would fail with "no such column". This
            // trimmed sibling context maps only what ChatPersistence actually created.
            await using var seed = new ChatOnlyDbContext(
                new DbContextOptionsBuilder<ChatOnlyDbContext>().UseSqlite(ConnectionString).Options);
            seed.Chats.Add(new Chat
            {
                Id = chatId,
                Title = "quarterly revenue",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                LastMessageAt = DateTime.UtcNow,
            });
            seed.ChatMessages.Add(new ChatMessage
            {
                Id = messageId,
                ChatId = chatId,
                Sequence = 0,
                Role = ChatRole.User,
                Text = "how did we do last quarter?",
                CreatedAt = DateTime.UtcNow,
                OutcomeKind = ChatOutcomeKind.None,
            });
            await seed.SaveChangesAsync();
        }
        SqliteConnection.ClearAllPools();

        await using var migrated = NewContext();
        await StoreInitializer.InitializeAsync(migrated, NullLogger.Instance);

        var chat = await migrated.Chats.SingleAsync();
        Assert.Equal(chatId, chat.Id);
        Assert.Equal("quarterly revenue", chat.Title);
        // The rebuilt table's new column, unset by the seed above: proof the rebuild actually ran, not
        // just that the row it copied still has a title.
        Assert.Null(chat.ProjectId);

        var message = await migrated.ChatMessages.SingleAsync();
        Assert.Equal(messageId, message.Id);
        Assert.Equal(chatId, message.ChatId);
        Assert.Equal("how did we do last quarter?", message.Text);
    }

    [Fact]
    public async Task A_migration_failure_is_logged_with_the_file_path_not_the_full_connection_string()
    {
        // Regression test: the catch block used to log GetDbConnection().ConnectionString, which echoes
        // back everything the store was opened with, not just "which file". A store built via
        // EnsureCreatedAsync already has every current table (Chats included) but no history rows, so
        // StoreInitializer stamps InitialCreate as a false baseline and then MigrateAsync tries to
        // CREATE TABLE Chats over one that is already there — a reliable, deterministic failure with no
        // need to corrupt anything by hand.
        await using (var alreadyCurrent = NewContext())
            await alreadyCurrent.Database.EnsureCreatedAsync();
        SqliteConnection.ClearAllPools();

        var provider = new RecordingLoggerProvider();
        var logger = provider.CreateLogger("StoreInitializer");

        await using var db = NewContext();
        await Assert.ThrowsAnyAsync<Exception>(() => StoreInitializer.InitializeAsync(db, logger));

        var error = Assert.Single(provider.Records, r => r.Level == LogLevel.Error);
        Assert.Contains(DbPath, error.Message);
        // DataSource is the bare path; ConnectionString would additionally carry the "Data Source=" ADO.NET
        // keyword (and, for a store opened with more than that, whatever else rode along with it).
        Assert.DoesNotContain("Data Source=", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_rescued_legacy_store_ends_up_schema_identical_to_one_migrated_from_nothing()
    {
        // A_rescued_legacy_store_also_accepts_the_next_migration (above) only proves ChatPersistence's own
        // SQL does not error out against the shim's stamp — a table that is merely close enough (an extra
        // column, a missing index, a widened type) could still let CREATE TABLE / ALTER TABLE succeed
        // without actually matching what InitialCreate would have produced. Nothing before this test
        // checked the shim's real premise: that the six legacy tables it found are shaped exactly like
        // InitialCreate would have made them. Comparing PRAGMA table_info/index_list per table (schema,
        // not row data) against a store built by letting the migrations create everything from nothing is
        // what actually catches a shim whose stamp and the real prior schema have quietly drifted apart.
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

        await using (var rescued = NewContext())
            await StoreInitializer.InitializeAsync(rescued, NullLogger.Instance);

        var freshDir = Path.Combine(Path.GetTempPath(), $"sqlagent-migr-fresh-{Guid.NewGuid():N}");
        Directory.CreateDirectory(freshDir);
        var freshConnectionString = $"Data Source={Path.Combine(freshDir, "sqlagent.db")}";
        try
        {
            await using (var fresh = new SqlAgentDbContext(
                new DbContextOptionsBuilder<SqlAgentDbContext>().UseSqlite(freshConnectionString).Options))
                await StoreInitializer.InitializeAsync(fresh, NullLogger.Instance);
            SqliteConnection.ClearAllPools();

            var rescuedSchema = await SchemaFingerprintAsync(ConnectionString);
            var freshSchema = await SchemaFingerprintAsync(freshConnectionString);

            // Same tables...
            Assert.Equal(freshSchema.Keys, rescuedSchema.Keys);
            // ...and each one shaped the same way: columns (name, type, not-null, default, pk position)
            // and indexes (name, uniqueness, member columns), not just present under the same name.
            foreach (var table in freshSchema.Keys)
                Assert.Equal(freshSchema[table], rescuedSchema[table]);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(freshDir)) Directory.Delete(freshDir, recursive: true);
        }

        // Confirms the comparison above was not vacuously true because the rescue silently dropped the
        // user's data — same guarantee the other rescue tests make, kept here so a future edit cannot
        // narrow this test into a schema check that no longer proves anything actually got rescued.
        await using var check = NewContext();
        Assert.Equal(connectionId, (await check.DatabaseConnections.SingleAsync()).Id);
    }

    /// <summary>Table name -> a string capturing every column (cid, name, type, not-null, default, pk
    /// position) and every index (name, uniqueness, member columns in order) for that table, read straight
    /// from SQLite's own PRAGMAs rather than sqlite_master's CREATE TABLE text — which two schemas built
    /// through different paths (EnsureCreated vs. a migration's generated SQL) are not guaranteed to
    /// render identically even when the resulting schema is. PRAGMA table_info/index_list report the
    /// engine's own understanding of the schema, which is the thing that actually has to match.</summary>
    private static async Task<SortedDictionary<string, string>> SchemaFingerprintAsync(string connectionString)
    {
        await using var conn = new SqliteConnection(connectionString);
        await conn.OpenAsync();

        var tables = new List<string>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name";
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync()) tables.Add(reader.GetString(0));
        }

        var fingerprint = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var table in tables)
        {
            var sb = new StringBuilder();
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = $"PRAGMA table_info('{table}')";
                await using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    sb.Append(reader.GetInt64(0)).Append('|')
                        .Append(reader.GetString(1)).Append('|')
                        .Append(reader.GetString(2)).Append('|')
                        .Append(reader.GetInt64(3)).Append('|')
                        .Append(reader.IsDBNull(4) ? "" : Convert.ToString(reader.GetValue(4))).Append('|')
                        .Append(reader.GetInt64(5)).Append(';');
                }
            }

            var indexes = new List<(string Name, long Unique)>();
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = $"PRAGMA index_list('{table}')";
                await using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                    indexes.Add((reader.GetString(1), reader.GetInt64(2)));
            }
            // sqlite_autoindex_* entries back an inline UNIQUE/PK column constraint rather than a named
            // index the model declared; their generated names embed a per-table sequence number that has
            // no reason to line up between a rescued and a from-scratch store, even when the underlying
            // constraint is identical. Recorded by column membership and uniqueness instead of by name.
            foreach (var (indexName, unique) in indexes.OrderBy(i => i.Name, StringComparer.Ordinal))
            {
                var cols = new List<string>();
                await using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = $"PRAGMA index_info('{indexName}')";
                    await using var reader = await cmd.ExecuteReaderAsync();
                    while (await reader.ReadAsync()) cols.Add(reader.GetString(2));
                }
                var label = indexName.StartsWith("sqlite_autoindex_", StringComparison.Ordinal)
                    ? "autoindex" : indexName;
                sb.Append("idx:").Append(label).Append('=').Append(unique).Append(':')
                    .Append(string.Join(",", cols)).Append(';');
            }

            fingerprint[table] = sb.ToString();
        }

        return fingerprint;
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

/// <summary>
/// The store as it was right after Phase B1's ChatPersistence migration: Chats and ChatMessages exist, but
/// Chat has no ProjectId column and there is no Projects table yet — Phase B2's own migration is what adds
/// both. Used only to seed through a model that matches that schema; the actual tables are created by the
/// real migrator (see <see cref="StoreMigrationTests.A_chat_and_its_message_survive_the_migration_that_rebuilds_the_Chats_table"/>),
/// not by this context's own OnModelCreating.
/// </summary>
public sealed class ChatOnlyDbContext(DbContextOptions<ChatOnlyDbContext> options) : DbContext(options)
{
    public DbSet<Chat> Chats => Set<Chat>();
    public DbSet<ChatMessage> ChatMessages => Set<ChatMessage>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Chat>(e =>
        {
            e.HasKey(x => x.Id);
            e.Ignore(x => x.ProjectId);
            e.Ignore(x => x.Project);
        });
        b.Entity<ChatMessage>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasOne(x => x.Chat).WithMany(c => c.Messages)
                .HasForeignKey(x => x.ChatId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.Role).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.OutcomeKind).HasConversion<string>().HasMaxLength(32);
        });
    }
}
