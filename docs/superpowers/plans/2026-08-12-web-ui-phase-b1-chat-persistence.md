# Web UI Phase B1 — Chat Persistence, Message-Level Database Context, History

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give the chat a store — EF Core migrations, persisted chats and messages, the set of databases each message was sent with — plus a real chat page at `/`, day-bucketed history in the sidebar, and the SQL editor parked permanently at `/sql`.

**Architecture:** Two new services in `SqlAgent.Storage`: `ChatService` (CRUD and history) and `ChatTurnService` (one turn end to end). Both are scoped and run through the existing `ScopedRunner`, so no `DbContext` outlives a single user action. The UI is Blazor components under `Components/Shared/Chat/` plus a `HistorySection` in the sidebar. Databases attach to a *message*: the composer holds chips in circuit state, each send writes a snapshot row per database, and chips carry over to the next message.

**Tech Stack:** .NET 10, Blazor Server (Interactive Server on `<Routes>`), EF Core 10 + SQLite, xUnit + bUnit, `WebApplicationFactory` for integration tests. No Node, no npm, no CDN.

**Spec:** `docs/superpowers/specs/2026-08-12-web-ui-phase-b1-chat-persistence-design.md`

## Global Constraints

- **Target framework `net10.0`.** Nullable enabled, implicit usings enabled.
- **No Node toolchain, no npm.** CI runs only `dotnet restore/build/test` against `SqlAgent.slnx`.
- **No CDN, no external network at runtime.** Every asset ships in `wwwroot`.
- **Components consume `var(--token)` only — never a literal color.** `RestyleRegressionTests.No_component_stylesheet_hard_codes_a_hex_color` enforces it across every `*.razor.css`.
- **Provider and exception text is never rendered to the user.** It can echo a connection string. Log it, show a stable code and a fixed message.
- **Result rows are never persisted.** Only text, generated SQL, and the row-count / duration / truncation metadata.
- **A migration failure stops the host.** Log the store path, rethrow.
- **Every existing test must stay green** unless this plan says which test changes and why. Tasks 5, 6, 7, 9 and 10 each name the tests they extend, rewrite, or retire; no other test may be touched.
- **Test conventions:** xUnit, `Bunit.TestContext`, sentence-style test names with underscores, one class per unit under test in `tests/SqlAgent.Tests/`. Comments explain *why* a test exists, not what it does.
- **bUnit runs no browser, no CSS engine, no focus model.** Anything depending on those is asserted on rendered DOM structure or on stylesheet source text, or moved to the manual checklist in `docs/web-ui.md`.
- **Commit messages** end with `Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>`.
- **Verification command:** `dotnet test SqlAgent.slnx --configuration Release`

## File Structure

| File | Responsibility | Task |
|---|---|---|
| `.config/dotnet-tools.json` | Pins `dotnet-ef` so migrations are reproducible | 1 |
| `src/SqlAgent.Storage/SqlAgentDbContextFactory.cs` | Design-time factory, so `dotnet ef` never boots the web host | 1 |
| `src/SqlAgent.Storage/StoreInitializer.cs` | Baseline shim + `MigrateAsync` | 1 |
| `src/SqlAgent.Storage/Migrations/*` | Generated: `InitialCreate` (today's model), then `ChatPersistence` | 1, 2 |
| `src/SqlAgent.Host/Program.cs:54-55` | `StoreInitializer` replaces `EnsureCreatedAsync` | 1 |
| `tests/SqlAgent.Tests/StoreMigrationTests.cs` | A store born of `EnsureCreated` migrates and keeps its data | 1 |
| `src/SqlAgent.Storage/ChatEntities.cs` | `Chat`, `ChatMessage`, `ChatMessageDatabase`, their enums | 2 |
| `src/SqlAgent.Storage/SqlAgentDbContext.cs` | Three `DbSet`s, keys, indexes, cascade | 2 |
| `src/SqlAgent.Storage/ChatService.cs` | History, read, create, append, rename, delete | 2 |
| `tests/SqlAgent.Tests/ChatServiceTests.cs` | Sequence, ordering, cascade, snapshots | 2 |
| `src/SqlAgent.Storage/ChatHistoryGrouping.cs` | Pure day-bucketing over `ChatSummary` | 3 |
| `tests/SqlAgent.Tests/ChatHistoryGroupingTests.cs` | Boundaries: midnight, 7 days, 30 days, DST | 3 |
| `src/SqlAgent.Storage/ChatTurnService.cs` | One turn: persist, branch on attachments, persist | 4 |
| `tests/SqlAgent.Tests/ChatTurnServiceTests.cs` | Zero / one / several databases, gateway failure | 4 |
| `src/SqlAgent.Host/Components/Shared/Ui/Icon.razor` | Eight new glyphs | 5 |
| `src/SqlAgent.Host/Web/AppState.cs` | `ActiveChatId`, `ChatsChanged`, `PendingSql` | 5 |
| `src/SqlAgent.Host/Web/DialogService.cs` | Circuit-scoped current dialog | 5 |
| `src/SqlAgent.Host/Components/Layout/DialogHost.razor` | Renders it from `MainLayout`, outside the sidebar | 5 |
| `src/SqlAgent.Host/wwwroot/css/app.css` | Collapsed rules scoped under the sidebar | 6 |
| `src/SqlAgent.Host/Components/Layout/Sidebar.razor` | Closed drawer leaves the tab order; focus returns | 6 |
| `tests/SqlAgent.Tests/SidebarCollapseParityTests.cs` | The two collapsed rule sets cannot drift | 6 |
| `src/SqlAgent.Host/Components/Pages/Workspace.razor` | Tab strip gone, route `/sql` | 7 |
| `src/SqlAgent.Host/Components/Layout/SidebarNav.razor` | New chat, SQL, Connections, Settings | 7 |
| `src/SqlAgent.Host/Components/Shared/Chat/AttachmentChips.razor` | Chip row above the textarea | 8 |
| `src/SqlAgent.Host/Components/Shared/Chat/AttachmentMenu.razor` | Databases section, empty state | 8 |
| `src/SqlAgent.Host/Components/Shared/Chat/Composer.razor` | Textarea, Enter to send, send/stop | 8 |
| `src/SqlAgent.Host/Components/Shared/Chat/UserMessage.razor` | Right-aligned pill + its chips | 9 |
| `src/SqlAgent.Host/Components/Shared/Chat/AssistantMessage.razor` | Live result, or a restored one | 9 |
| `src/SqlAgent.Host/Components/Shared/ChatOutcome.razor` | `Restored` parameter: metadata, not an empty grid | 9 |
| `src/SqlAgent.Host/Components/Pages/Chat.razor` | `/` and `/chat/{id}` | 9 |
| `src/SqlAgent.Host/Components/Layout/HistorySection.razor` | Buckets, active row, `⋮` menu | 10 |
| `docs/web-ui.md`, `README.md` | Chat persistence, attachments, manual checks | 11 |

---

### Task 1: EF Core migrations and the baseline shim

**Files:**
- Create: `.config/dotnet-tools.json`
- Create: `src/SqlAgent.Storage/SqlAgentDbContextFactory.cs`
- Create: `src/SqlAgent.Storage/StoreInitializer.cs`
- Generate: `src/SqlAgent.Storage/Migrations/*_InitialCreate.cs`
- Modify: `src/SqlAgent.Storage/SqlAgent.Storage.csproj`
- Modify: `src/SqlAgent.Host/Program.cs:54-55`
- Test: `tests/SqlAgent.Tests/StoreMigrationTests.cs`

**Interfaces:**
- Consumes: `SqlAgentDbContext`, the six existing entities.
- Produces:
  - `SqlAgent.Storage.StoreInitializer.InitializeAsync(SqlAgentDbContext db, ILogger logger, CancellationToken ct = default): Task` — stamps the baseline when needed, then migrates. Throws on failure after logging.
  - `SqlAgent.Storage.SqlAgentDbContextFactory : IDesignTimeDbContextFactory<SqlAgentDbContext>`.

- [ ] **Step 1: Write the failing test**

Create `tests/SqlAgent.Tests/StoreMigrationTests.cs`:

```csharp
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
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
        await using var db = NewContext();
        await StoreInitializer.InitializeAsync(db, NullLogger.Instance);

        await StoreInitializer.InitializeAsync(db, NullLogger.Instance);

        Assert.NotEmpty(await db.Database.GetAppliedMigrationsAsync());
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
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/SqlAgent.Tests/SqlAgent.Tests.csproj --filter StoreMigrationTests`
Expected: FAIL — compile error, `StoreInitializer` does not exist.

- [ ] **Step 3: Add the EF design-time package and the tool manifest**

In `src/SqlAgent.Storage/SqlAgent.Storage.csproj`, add to the existing `PackageReference` group:

```xml
    <!-- Design-time only: `dotnet ef migrations add` needs it, the shipped app does not, so
         PrivateAssets keeps it out of the output and out of every consuming project. -->
    <PackageReference Include="Microsoft.EntityFrameworkCore.Design" Version="10.0.0" PrivateAssets="all" />
```

Then pin the CLI so a migration generated on one machine matches one generated on another:

```bash
dotnet new tool-manifest
dotnet tool install dotnet-ef --version 10.0.0
```

- [ ] **Step 4: Write the design-time factory**

Create `src/SqlAgent.Storage/SqlAgentDbContextFactory.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace SqlAgent.Storage;

/// <summary>
/// How `dotnet ef` builds a context at design time. Without it EF falls back to the startup project's
/// entry point through HostFactoryResolver, which means scaffolding a migration depends on the whole web
/// host booting far enough to call builder.Build() — including UseStaticWebAssets, the Windows-service
/// registrations and the loopback URL resolution, none of which have anything to do with the model. The
/// connection string here is never opened by `migrations add`; it exists because DbContextOptions demands
/// one, and it deliberately points at a scratch file rather than the real store so a mistyped `database
/// update` cannot touch anyone's data.
/// </summary>
public sealed class SqlAgentDbContextFactory : IDesignTimeDbContextFactory<SqlAgentDbContext>
{
    public SqlAgentDbContext CreateDbContext(string[] args) => new(
        new DbContextOptionsBuilder<SqlAgentDbContext>()
            .UseSqlite("Data Source=sqlagent-design-time.db")
            .Options);
}
```

- [ ] **Step 5: Generate the initial migration**

It must describe **today's** model exactly — the six original entities and nothing else. The chat tables
arrive in Task 2 as a second migration; folding them in here would make Step 6's shim stamp them as
already applied and they would never be created on an existing store.

```bash
dotnet ef migrations add InitialCreate --project src/SqlAgent.Storage
```

Do not hand-write the generated files. Verify the result before continuing:

```bash
grep -c 'CreateTable' src/SqlAgent.Storage/Migrations/*_InitialCreate.cs
```

Expected: `6` — `DatabaseConnections`, `TablePolicies`, `SchemaCaches`, `QueryAuditLogs`, `AppSettings`,
`Secrets`. If the count differs, the model changed under you; stop and reconcile before proceeding.

- [ ] **Step 6: Write `StoreInitializer`**

Create `src/SqlAgent.Storage/StoreInitializer.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging;

namespace SqlAgent.Storage;

/// <summary>
/// Brings the local store up to the current schema at startup.
///
/// The hard case is the store that already exists. Until Phase B1 the host called EnsureCreatedAsync,
/// which creates tables and records nothing — so a store in the field has the six original tables and no
/// __EFMigrationsHistory at all. MigrateAsync against one of those replays InitialCreate, hits
/// "table DatabaseConnections already exists" and throws on startup. The fix is to write the history row
/// the old code path never wrote, exactly once, and only for a store that is genuinely in that state.
/// </summary>
public static class StoreInitializer
{
    private const string HistoryTable = "__EFMigrationsHistory";

    /// <summary>Any table only the pre-migration schema could have created. Its presence, together with
    /// a missing history table, is what identifies an EnsureCreated store.</summary>
    private const string LegacyMarkerTable = "DatabaseConnections";

    public static async Task InitializeAsync(
        SqlAgentDbContext db, ILogger logger, CancellationToken ct = default)
    {
        try
        {
            var tables = await ListTablesAsync(db, ct);

            if (!tables.Contains(HistoryTable) && tables.Contains(LegacyMarkerTable))
            {
                // GetMigrations() returns the migrations compiled into this assembly in id order, so the
                // first is InitialCreate. Reading it here rather than hardcoding the timestamped id keeps
                // this correct if the migration is ever regenerated.
                var baseline = db.Database.GetMigrations().First();
                logger.LogInformation(
                    "Store predates migrations; stamping {Migration} as applied before migrating.", baseline);
                await StampAsync(db, baseline, ct);
            }

            await db.Database.MigrateAsync(ct);
        }
        catch (Exception ex)
        {
            // A half-migrated store loses or corrupts data on every subsequent write, so this is fatal by
            // design (see the spec's decision table). The connection string is logged because the first
            // question anyone asks is "which store?" — it is a local file path, not a secret; the
            // database passwords live in ISecretStore and never appear here.
            logger.LogError(ex, "The local store at {Store} could not be migrated. The host cannot start.",
                db.Database.GetDbConnection().ConnectionString);
            throw;
        }
    }

    /// <summary>Table names in the SQLite file. The alias to "Value" is required: EF's scalar SqlQueryRaw
    /// projects a single column by that name and throws otherwise.</summary>
    private static async Task<HashSet<string>> ListTablesAsync(SqlAgentDbContext db, CancellationToken ct)
    {
        var names = await db.Database
            .SqlQueryRaw<string>("SELECT name AS Value FROM sqlite_master WHERE type = 'table'")
            .ToListAsync(ct);
        return names.ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>Writes the history table and one row, using EF's own scripts rather than hand-rolled DDL
    /// so the table shape is whatever this EF version expects to read back.</summary>
    private static async Task StampAsync(SqlAgentDbContext db, string migrationId, CancellationToken ct)
    {
        var history = db.GetService<IHistoryRepository>();
        var version = typeof(DbContext).Assembly.GetName().Version?.ToString() ?? "10.0.0";

        await db.Database.ExecuteSqlRawAsync(history.GetCreateIfNotExistsScript(), ct);
        await db.Database.ExecuteSqlRawAsync(
            history.GetInsertScript(new HistoryRow(migrationId, version)), ct);
    }
}
```

- [ ] **Step 7: Run the tests to verify they pass**

Run: `dotnet test tests/SqlAgent.Tests/SqlAgent.Tests.csproj --filter StoreMigrationTests`
Expected: PASS, 3 tests.

- [ ] **Step 8: Wire it into `Program.cs`**

Replace lines 54-55 of `src/SqlAgent.Host/Program.cs`:

```csharp
using (var scope = app.Services.CreateScope())
{
    // Migrations, not EnsureCreated: EnsureCreated never alters a store that already exists, so every
    // schema change after the first would silently never reach a machine that had run an earlier build.
    // StoreInitializer also carries the one-time baseline stamp for stores created the old way.
    var db = scope.ServiceProvider.GetRequiredService<SqlAgentDbContext>();
    await StoreInitializer.InitializeAsync(db, app.Logger);
}
```

- [ ] **Step 9: Run the whole suite**

Run: `dotnet test SqlAgent.slnx --configuration Release`
Expected: PASS. `WebTestHost` boots the real `Program`, so every integration test now exercises the
migration path against a fresh store.

- [ ] **Step 10: Commit**

```bash
git add .config src/SqlAgent.Storage src/SqlAgent.Host/Program.cs tests/SqlAgent.Tests/StoreMigrationTests.cs
git commit -m "$(cat <<'EOF'
Replace EnsureCreated with EF Core migrations and a baseline shim

EnsureCreatedAsync never alters a store that already exists, so the chat tables
Phase B1 adds would silently never appear on any machine that had run an earlier
build — and Phase C adds columns to TablePolicy, which would fail the same way.

InitialCreate describes today's six entities exactly. StoreInitializer stamps it
as applied on a store that has those tables but no __EFMigrationsHistory (the
signature of EnsureCreated) and then migrates, so an existing store keeps its
data instead of failing on "table already exists". A failed migration stops the
host: a half-migrated store loses data on every subsequent write.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 2: Chat entities and `ChatService`

**Files:**
- Create: `src/SqlAgent.Storage/ChatEntities.cs`
- Create: `src/SqlAgent.Storage/ChatService.cs`
- Modify: `src/SqlAgent.Storage/SqlAgentDbContext.cs`
- Generate: `src/SqlAgent.Storage/Migrations/*_ChatPersistence.cs`
- Modify: `src/SqlAgent.Host/Program.cs` (register `ChatService`)
- Test: `tests/SqlAgent.Tests/ChatServiceTests.cs`

**Interfaces:**
- Consumes: `SqlAgentDbContext`, `StoreInitializer` from Task 1.
- Produces:
  - `enum ChatRole { User, Assistant }`
  - `enum ChatOutcomeKind { None, QueryResult, Clarification, Error }`
  - `record ChatDatabaseRef(Guid? ConnectionId, string Name)`
  - `record ChatSummary(Guid Id, string Title, DateTime LastMessageAt)`
  - `record ChatMessageView(Guid Id, int Sequence, ChatRole Role, string Text, DateTime CreatedAt, string? GeneratedSql, ChatOutcomeKind OutcomeKind, string? ErrorCode, int? RowCount, long? ElapsedMs, bool Truncated, IReadOnlyList<ChatDatabaseRef> Databases)`
  - `record ChatDetail(Guid Id, string Title, IReadOnlyList<ChatMessageView> Messages)`
  - `record ChatMessageInput(Guid ChatId, ChatRole Role, string Text, IReadOnlyList<ChatDatabaseRef> Databases, string? GeneratedSql = null, ChatOutcomeKind OutcomeKind = ChatOutcomeKind.None, string? ErrorCode = null, int? RowCount = null, long? ElapsedMs = null, bool Truncated = false)`
  - `ChatService.ListHistoryAsync(int take = 200, CancellationToken ct = default): Task<IReadOnlyList<ChatSummary>>`
  - `ChatService.GetChatAsync(Guid id, CancellationToken ct = default): Task<ChatDetail?>`
  - `ChatService.CreateChatAsync(string title, CancellationToken ct = default): Task<Guid>`
  - `ChatService.AppendMessageAsync(ChatMessageInput input, CancellationToken ct = default): Task<ChatMessageView>`
  - `ChatService.RenameChatAsync(Guid id, string title, CancellationToken ct = default): Task<bool>`
  - `ChatService.DeleteChatAsync(Guid id, CancellationToken ct = default): Task<bool>`
  - `ChatService.TitleFrom(string firstMessage): string` — first 60 characters, trimmed.

- [ ] **Step 1: Write the failing test**

Create `tests/SqlAgent.Tests/ChatServiceTests.cs`:

```csharp
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SqlAgent.Storage;

namespace SqlAgent.Tests;

public class ChatServiceTests : IDisposable
{
    private readonly SqliteConnection _conn = new("DataSource=:memory:");
    private readonly SqlAgentDbContext _db;
    private readonly ChatService _chats;

    public ChatServiceTests()
    {
        _conn.Open();
        _db = new SqlAgentDbContext(
            new DbContextOptionsBuilder<SqlAgentDbContext>().UseSqlite(_conn).Options);
        _db.Database.EnsureCreated();
        _chats = new ChatService(_db);
    }

    [Fact]
    public async Task Messages_are_numbered_in_the_order_they_are_appended()
    {
        // Sequence is what orders a reloaded transcript. CreatedAt is not a substitute: two messages
        // written inside the same millisecond would reload in arbitrary order, and a turn is exactly
        // that — the question and its answer are appended back to back.
        var chat = await _chats.CreateChatAsync("t");

        await _chats.AppendMessageAsync(new ChatMessageInput(chat, ChatRole.User, "q", []));
        await _chats.AppendMessageAsync(new ChatMessageInput(chat, ChatRole.Assistant, "a", []));

        var detail = await _chats.GetChatAsync(chat);
        Assert.Equal([0, 1], detail!.Messages.Select(m => m.Sequence));
        Assert.Equal([ChatRole.User, ChatRole.Assistant], detail.Messages.Select(m => m.Role));
    }

    [Fact]
    public async Task Appending_a_message_moves_the_chat_to_the_top_of_history()
    {
        var older = await _chats.CreateChatAsync("older");
        var newer = await _chats.CreateChatAsync("newer");
        await _chats.AppendMessageAsync(new ChatMessageInput(newer, ChatRole.User, "q", []));
        await _chats.AppendMessageAsync(new ChatMessageInput(older, ChatRole.User, "q", []));

        var history = await _chats.ListHistoryAsync();

        Assert.Equal("older", history[0].Title);
    }

    [Fact]
    public async Task A_message_keeps_the_databases_it_was_sent_with()
    {
        // The whole point of the attachment snapshot: a transcript that cannot say which database a
        // question was asked against is not an audit trail.
        var chat = await _chats.CreateChatAsync("t");
        var id = Guid.NewGuid();

        await _chats.AppendMessageAsync(new ChatMessageInput(
            chat, ChatRole.User, "q", [new ChatDatabaseRef(id, "prod"), new ChatDatabaseRef(null, "gone")]));

        var message = (await _chats.GetChatAsync(chat))!.Messages.Single();
        Assert.Equal(["gone", "prod"], message.Databases.Select(d => d.Name).OrderBy(n => n));
        Assert.Equal(id, message.Databases.Single(d => d.Name == "prod").ConnectionId);
    }

    [Fact]
    public async Task Deleting_a_chat_takes_its_messages_and_their_attachments_with_it()
    {
        var chat = await _chats.CreateChatAsync("t");
        await _chats.AppendMessageAsync(new ChatMessageInput(
            chat, ChatRole.User, "q", [new ChatDatabaseRef(Guid.NewGuid(), "prod")]));

        Assert.True(await _chats.DeleteChatAsync(chat));

        Assert.Null(await _chats.GetChatAsync(chat));
        Assert.Empty(await _db.ChatMessages.ToListAsync());
        Assert.Empty(await _db.ChatMessageDatabases.ToListAsync());
    }

    [Fact]
    public async Task Renaming_a_chat_changes_only_its_title()
    {
        var chat = await _chats.CreateChatAsync("first question, truncated");
        await _chats.AppendMessageAsync(new ChatMessageInput(chat, ChatRole.User, "q", []));

        Assert.True(await _chats.RenameChatAsync(chat, "quarterly revenue"));

        var detail = await _chats.GetChatAsync(chat);
        Assert.Equal("quarterly revenue", detail!.Title);
        Assert.Single(detail.Messages);
    }

    [Fact]
    public async Task Renaming_or_deleting_a_chat_that_is_gone_reports_it_rather_than_throwing()
    {
        // Two tabs, one chat: deleting it in the first leaves the second holding a stale row. That is an
        // ordinary race in a UI with a sidebar, not an exceptional condition.
        Assert.False(await _chats.RenameChatAsync(Guid.NewGuid(), "x"));
        Assert.False(await _chats.DeleteChatAsync(Guid.NewGuid()));
    }

    [Fact]
    public void A_title_is_the_first_message_cut_to_sixty_characters()
    {
        // There is no model to summarize with, so the first question is the title. 60 is what the
        // sidebar row fits before ellipsis.
        Assert.Equal("short one", ChatService.TitleFrom("  short one  "));
        Assert.Equal(60, ChatService.TitleFrom(new string('x', 200)).Length);
        Assert.Equal("Untitled chat", ChatService.TitleFrom("   "));
    }

    [Fact]
    public async Task An_error_answer_persists_its_code_and_the_metadata_but_no_rows()
    {
        // Rows are never stored (see the spec). What must survive is enough to render the answer again:
        // the code, the SQL, and the numbers.
        var chat = await _chats.CreateChatAsync("t");

        await _chats.AppendMessageAsync(new ChatMessageInput(
            chat, ChatRole.Assistant, "Query was canceled.", [],
            GeneratedSql: "SELECT 1", OutcomeKind: ChatOutcomeKind.Error,
            ErrorCode: "execution_canceled", RowCount: null, ElapsedMs: 12, Truncated: false));

        var message = (await _chats.GetChatAsync(chat))!.Messages.Single();
        Assert.Equal(ChatOutcomeKind.Error, message.OutcomeKind);
        Assert.Equal("execution_canceled", message.ErrorCode);
        Assert.Equal("SELECT 1", message.GeneratedSql);
        Assert.Equal(12, message.ElapsedMs);
    }

    public void Dispose()
    {
        _db.Dispose();
        _conn.Dispose();
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/SqlAgent.Tests/SqlAgent.Tests.csproj --filter ChatServiceTests`
Expected: FAIL — compile error, `ChatService` and the chat types do not exist.

- [ ] **Step 3: Write the entities**

Create `src/SqlAgent.Storage/ChatEntities.cs`:

```csharp
namespace SqlAgent.Storage;

/// <summary>Who wrote a message.</summary>
public enum ChatRole { User, Assistant }

/// <summary>
/// What an assistant message carries. Only the values Phase B1 can produce: Phase D adds
/// ConfirmationRequired and SchemaDiagram alongside the components that render them, so no member here
/// is ever written by nothing.
/// </summary>
public enum ChatOutcomeKind { None, QueryResult, Clarification, Error }

/// <summary>A conversation. Databases belong to its messages, not to it.</summary>
public class Chat
{
    public Guid Id { get; set; }
    public string Title { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime LastMessageAt { get; set; }
    public List<ChatMessage> Messages { get; set; } = [];
}

/// <summary>
/// One message. Result rows are deliberately absent — <see cref="RowCount"/>, <see cref="ElapsedMs"/>
/// and <see cref="Truncated"/> are what a reloaded transcript shows instead, so the local store never
/// becomes a shadow copy of production data.
/// </summary>
public class ChatMessage
{
    public Guid Id { get; set; }
    public Guid ChatId { get; set; }
    public Chat? Chat { get; set; }

    /// <summary>Zero-based position in the conversation. Ordering by CreatedAt is not enough: a question
    /// and its answer are written back to back and can share a millisecond.</summary>
    public int Sequence { get; set; }

    public ChatRole Role { get; set; }
    public string Text { get; set; } = "";
    public DateTime CreatedAt { get; set; }

    public string? GeneratedSql { get; set; }
    public ChatOutcomeKind OutcomeKind { get; set; }

    /// <summary>Stable code from the service layer. The user-safe message lives in <see cref="Text"/>.</summary>
    public string? ErrorCode { get; set; }

    public int? RowCount { get; set; }
    public long? ElapsedMs { get; set; }
    public bool Truncated { get; set; }

    public List<ChatMessageDatabase> Databases { get; set; } = [];
}

/// <summary>
/// One database attached to one message.
///
/// <see cref="DatabaseConnectionId"/> is nullable and <see cref="DatabaseName"/> is not, on purpose:
/// connections get renamed and deleted, and a transcript that forgets what a question was asked against
/// is worse than a dangling id. Deleting a connection nulls the id across history and leaves the name.
/// </summary>
public class ChatMessageDatabase
{
    public Guid Id { get; set; }
    public Guid ChatMessageId { get; set; }
    public ChatMessage? Message { get; set; }
    public Guid? DatabaseConnectionId { get; set; }
    public string DatabaseName { get; set; } = "";
}
```

- [ ] **Step 4: Configure them on the context**

In `src/SqlAgent.Storage/SqlAgentDbContext.cs`, add the three sets beside the existing six:

```csharp
    public DbSet<Chat> Chats => Set<Chat>();
    public DbSet<ChatMessage> ChatMessages => Set<ChatMessage>();
    public DbSet<ChatMessageDatabase> ChatMessageDatabases => Set<ChatMessageDatabase>();
```

and this configuration at the end of `OnModelCreating`:

```csharp
        b.Entity<Chat>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.LastMessageAt);
        });
        b.Entity<ChatMessage>(e =>
        {
            e.HasKey(x => x.Id);
            // Unique, not merely indexed: it is the backstop for two browser tabs appending to the same
            // chat at once. ChatService catches the violation and retries rather than writing two
            // messages that both claim the same position.
            e.HasIndex(x => new { x.ChatId, x.Sequence }).IsUnique();
            e.HasOne(x => x.Chat).WithMany(c => c.Messages)
                .HasForeignKey(x => x.ChatId).OnDelete(DeleteBehavior.Cascade);
            // Stored as text, like QueryAuditLog.Decision. An int column would silently re-interpret
            // every existing row the day someone inserts a member in the middle of either enum.
            e.Property(x => x.Role).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.OutcomeKind).HasConversion<string>().HasMaxLength(32);
        });
        b.Entity<ChatMessageDatabase>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.ChatMessageId);
            // One row per database per message. Names are unique across connections, so this also stops
            // the same database being attached twice from a double-click.
            e.HasIndex(x => new { x.ChatMessageId, x.DatabaseName }).IsUnique();
            e.HasOne(x => x.Message).WithMany(m => m.Databases)
                .HasForeignKey(x => x.ChatMessageId).OnDelete(DeleteBehavior.Cascade);
            // No foreign key to DatabaseConnection on purpose: the reference is a soft one that survives
            // the connection being deleted (the id is nulled, the name stays). A real FK with
            // SetNull would work too, but it would put a constraint on a table that has nothing to do
            // with chat and make DatabaseConnectionService's delete path depend on chat schema.
        });
```

- [ ] **Step 5: Write `ChatService`**

Create `src/SqlAgent.Storage/ChatService.cs`:

```csharp
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace SqlAgent.Storage;

/// <summary>A database attached to a message: the live connection id when it still exists, and always
/// the name it had when the message was sent.</summary>
public record ChatDatabaseRef(Guid? ConnectionId, string Name);

/// <summary>A history row. Deliberately without messages — the sidebar lists hundreds of these.</summary>
public record ChatSummary(Guid Id, string Title, DateTime LastMessageAt);

/// <summary>One message as the UI renders it. Rows are absent because rows are never stored.</summary>
public record ChatMessageView(
    Guid Id,
    int Sequence,
    ChatRole Role,
    string Text,
    DateTime CreatedAt,
    string? GeneratedSql,
    ChatOutcomeKind OutcomeKind,
    string? ErrorCode,
    int? RowCount,
    long? ElapsedMs,
    bool Truncated,
    IReadOnlyList<ChatDatabaseRef> Databases);

/// <summary>A whole conversation, messages in order.</summary>
public record ChatDetail(Guid Id, string Title, IReadOnlyList<ChatMessageView> Messages);

/// <summary>What a caller supplies to append one message.</summary>
public record ChatMessageInput(
    Guid ChatId,
    ChatRole Role,
    string Text,
    IReadOnlyList<ChatDatabaseRef> Databases,
    string? GeneratedSql = null,
    ChatOutcomeKind OutcomeKind = ChatOutcomeKind.None,
    string? ErrorCode = null,
    int? RowCount = null,
    long? ElapsedMs = null,
    bool Truncated = false);

/// <summary>
/// The chat store: history, one conversation, and appends. Orchestrating a turn — deciding what to do
/// with the attached databases and calling the model — is <see cref="ChatTurnService"/>'s job, kept
/// separate so this stays a store with no opinion about language models.
/// </summary>
public class ChatService(SqlAgentDbContext db)
{
    /// <summary>SQLite's constraint-violation result code. Raised here by the unique (ChatId, Sequence)
    /// index when two circuits append to one chat at the same moment.</summary>
    private const int SqliteConstraint = 19;

    public async Task<IReadOnlyList<ChatSummary>> ListHistoryAsync(
        int take = 200, CancellationToken ct = default) =>
        await db.Chats
            .OrderByDescending(c => c.LastMessageAt)
            .Take(take)
            .Select(c => new ChatSummary(c.Id, c.Title, c.LastMessageAt))
            .ToListAsync(ct);

    public async Task<ChatDetail?> GetChatAsync(Guid id, CancellationToken ct = default)
    {
        var chat = await db.Chats
            .AsNoTracking()
            .Include(c => c.Messages).ThenInclude(m => m.Databases)
            .FirstOrDefaultAsync(c => c.Id == id, ct);
        if (chat is null) return null;

        return new ChatDetail(chat.Id, chat.Title,
            chat.Messages.OrderBy(m => m.Sequence).Select(ToView).ToList());
    }

    public async Task<Guid> CreateChatAsync(string title, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var chat = new Chat { Id = Guid.NewGuid(), Title = title, CreatedAt = now, UpdatedAt = now, LastMessageAt = now };
        db.Chats.Add(chat);
        await db.SaveChangesAsync(ct);
        return chat.Id;
    }

    public async Task<ChatMessageView> AppendMessageAsync(
        ChatMessageInput input, CancellationToken ct = default)
    {
        try
        {
            return await AppendOnceAsync(input, ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqliteException { SqliteErrorCode: SqliteConstraint })
        {
            // Another circuit took the sequence number between the read and the write. Re-reading the
            // max and trying again is enough: the loser of the race simply lands after the winner. One
            // retry, not a loop — a second collision means something other than a two-tab race, and a
            // retry loop would hide it.
            db.ChangeTracker.Clear();
            return await AppendOnceAsync(input, ct);
        }
    }

    private async Task<ChatMessageView> AppendOnceAsync(ChatMessageInput input, CancellationToken ct)
    {
        var chat = await db.Chats.FirstOrDefaultAsync(c => c.Id == input.ChatId, ct)
            ?? throw new InvalidOperationException($"Chat {input.ChatId} does not exist.");

        var next = await db.ChatMessages
            .Where(m => m.ChatId == input.ChatId)
            .Select(m => (int?)m.Sequence)
            .MaxAsync(ct) is { } max ? max + 1 : 0;

        var now = DateTime.UtcNow;
        var message = new ChatMessage
        {
            Id = Guid.NewGuid(),
            ChatId = input.ChatId,
            Sequence = next,
            Role = input.Role,
            Text = input.Text,
            CreatedAt = now,
            GeneratedSql = input.GeneratedSql,
            OutcomeKind = input.OutcomeKind,
            ErrorCode = input.ErrorCode,
            RowCount = input.RowCount,
            ElapsedMs = input.ElapsedMs,
            Truncated = input.Truncated,
            Databases = input.Databases.Select(d => new ChatMessageDatabase
            {
                Id = Guid.NewGuid(),
                DatabaseConnectionId = d.ConnectionId,
                DatabaseName = d.Name,
            }).ToList(),
        };

        db.ChatMessages.Add(message);
        chat.LastMessageAt = now;
        chat.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        return ToView(message);
    }

    public async Task<bool> RenameChatAsync(Guid id, string title, CancellationToken ct = default)
    {
        var chat = await db.Chats.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (chat is null) return false;
        chat.Title = TitleFrom(title);
        chat.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> DeleteChatAsync(Guid id, CancellationToken ct = default)
    {
        var chat = await db.Chats.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (chat is null) return false;
        // Messages and their attachment rows go with it through the cascade configured on the context.
        db.Chats.Remove(chat);
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>The title for a chat named after its first question. There is no model to summarize
    /// with, so this is a trim and a cut, not a summary.</summary>
    public static string TitleFrom(string firstMessage)
    {
        var trimmed = firstMessage.Trim();
        if (trimmed.Length == 0) return "Untitled chat";
        return trimmed.Length <= 60 ? trimmed : trimmed[..60];
    }

    private static ChatMessageView ToView(ChatMessage m) => new(
        m.Id, m.Sequence, m.Role, m.Text, m.CreatedAt, m.GeneratedSql, m.OutcomeKind,
        m.ErrorCode, m.RowCount, m.ElapsedMs, m.Truncated,
        m.Databases.Select(d => new ChatDatabaseRef(d.DatabaseConnectionId, d.DatabaseName)).ToList());
}
```

- [ ] **Step 6: Generate the migration**

```bash
dotnet ef migrations add ChatPersistence --project src/SqlAgent.Storage
```

Verify it adds exactly the three tables and touches nothing else:

```bash
grep -E 'CreateTable|DropTable|AddColumn|DropColumn' src/SqlAgent.Storage/Migrations/*_ChatPersistence.cs
```

Expected: three `CreateTable` calls (`Chats`, `ChatMessages`, `ChatMessageDatabases`) and nothing else.
A `DropTable` or `AddColumn` here means the model drifted from `InitialCreate`; stop and reconcile.

- [ ] **Step 7: Register the service**

In `src/SqlAgent.Host/Program.cs`, beside the other scoped registrations:

```csharp
builder.Services.AddScoped<ChatService>();
```

- [ ] **Step 8: Run the tests to verify they pass**

Run: `dotnet test tests/SqlAgent.Tests/SqlAgent.Tests.csproj --filter ChatServiceTests`
Expected: PASS, 8 tests.

- [ ] **Step 9: Run the whole suite**

Run: `dotnet test SqlAgent.slnx --configuration Release`
Expected: PASS.

- [ ] **Step 10: Commit**

```bash
git add src/SqlAgent.Storage src/SqlAgent.Host/Program.cs tests/SqlAgent.Tests/ChatServiceTests.cs
git commit -m "$(cat <<'EOF'
Add the chat store: chats, messages, and per-message database snapshots

A message records the databases it was sent with — the live connection id when
it still exists, and always the name it had at the time. Deleting a connection
nulls the id and leaves the name, so a transcript can still say what a question
was asked against.

Sequence numbers the messages because CreatedAt cannot: a question and its answer
are appended back to back and can share a millisecond. The unique (ChatId,
Sequence) index is the backstop for two tabs appending at once, and the append
retries once when it fires.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 3: Day-bucketed history grouping

**Files:**
- Create: `src/SqlAgent.Storage/ChatHistoryGrouping.cs`
- Test: `tests/SqlAgent.Tests/ChatHistoryGroupingTests.cs`

**Interfaces:**
- Consumes: `ChatSummary` from Task 2.
- Produces:
  - `enum HistoryBucket { Today, Yesterday, Previous7Days, Previous30Days, Older }`
  - `record HistoryGroup(HistoryBucket Bucket, string Label, IReadOnlyList<ChatSummary> Chats)`
  - `ChatHistoryGrouping.BucketOf(DateTime lastMessageAtUtc, DateTime nowLocal): HistoryBucket`
  - `ChatHistoryGrouping.LabelOf(HistoryBucket bucket): string`
  - `ChatHistoryGrouping.Group(IEnumerable<ChatSummary> chats, DateTime nowLocal): IReadOnlyList<HistoryGroup>`

- [ ] **Step 1: Write the failing test**

Create `tests/SqlAgent.Tests/ChatHistoryGroupingTests.cs`:

```csharp
using SqlAgent.Storage;

namespace SqlAgent.Tests;

/// <summary>
/// Bucketing is arithmetic on calendar days, which is exactly the kind of code that looks obviously
/// right and is wrong at 00:30. A chat from 23:00 yesterday is "Yesterday" even though it is ninety
/// minutes old; a chat from 01:00 today is "Today" even though it is thirty minutes old. Nothing here
/// is about elapsed hours.
/// </summary>
public class ChatHistoryGroupingTests
{
    // A fixed local "now" with an awkward time of day: late enough that subtracting hours crosses
    // midnight backwards, early enough that adding them crosses forwards.
    private static readonly DateTime NowLocal = new(2026, 8, 12, 0, 30, 0, DateTimeKind.Local);

    private static HistoryBucket Bucket(DateTime local) =>
        ChatHistoryGrouping.BucketOf(local.ToUniversalTime(), NowLocal);

    [Fact]
    public void A_chat_from_ninety_minutes_ago_is_Yesterday_not_Today()
    {
        // 23:00 on the 11th, seen at 00:30 on the 12th. Elapsed-time arithmetic ("less than 24 hours is
        // today") gets this wrong, and it is the most common way this feature ships broken.
        Assert.Equal(HistoryBucket.Yesterday, Bucket(new DateTime(2026, 8, 11, 23, 0, 0, DateTimeKind.Local)));
    }

    [Fact]
    public void A_chat_from_thirty_minutes_ago_is_Today()
    {
        Assert.Equal(HistoryBucket.Today, Bucket(new DateTime(2026, 8, 12, 0, 0, 0, DateTimeKind.Local)));
    }

    [Theory]
    // Day offsets from "now" and the bucket each must land in. The interesting values are the edges:
    // 1 is the last Yesterday, 2 the first Previous7Days, 7 the last of it, 8 the first Previous30Days,
    // 30 the last of that, 31 the first Older.
    [InlineData(0, HistoryBucket.Today)]
    [InlineData(1, HistoryBucket.Yesterday)]
    [InlineData(2, HistoryBucket.Previous7Days)]
    [InlineData(7, HistoryBucket.Previous7Days)]
    [InlineData(8, HistoryBucket.Previous30Days)]
    [InlineData(30, HistoryBucket.Previous30Days)]
    [InlineData(31, HistoryBucket.Older)]
    [InlineData(400, HistoryBucket.Older)]
    public void Day_offsets_land_in_the_documented_buckets(int daysAgo, HistoryBucket expected)
    {
        Assert.Equal(expected, Bucket(NowLocal.Date.AddDays(-daysAgo).AddHours(9)));
    }

    [Fact]
    public void A_chat_dated_in_the_future_is_Today_rather_than_falling_off_the_end()
    {
        // Clock skew, a store copied from another machine, or a timezone change while the host runs. A
        // negative day difference must not fall through to Older, which would bury the newest chat at
        // the bottom of the list.
        Assert.Equal(HistoryBucket.Today, Bucket(NowLocal.AddHours(6)));
    }

    [Fact]
    public void Groups_come_back_newest_first_with_their_chats_newest_first_and_no_empty_group()
    {
        var chats = new[]
        {
            new ChatSummary(Guid.NewGuid(), "old", NowLocal.Date.AddDays(-40).ToUniversalTime()),
            new ChatSummary(Guid.NewGuid(), "today early", NowLocal.Date.AddMinutes(1).ToUniversalTime()),
            new ChatSummary(Guid.NewGuid(), "today late", NowLocal.Date.AddMinutes(20).ToUniversalTime()),
        };

        var groups = ChatHistoryGrouping.Group(chats, NowLocal);

        Assert.Equal([HistoryBucket.Today, HistoryBucket.Older], groups.Select(g => g.Bucket));
        Assert.Equal(["today late", "today early"], groups[0].Chats.Select(c => c.Title));
        // Yesterday and the two "previous" buckets are absent entirely rather than present and empty: a
        // heading with nothing under it reads as a rendering bug.
        Assert.DoesNotContain(groups, g => g.Chats.Count == 0);
    }

    [Fact]
    public void Every_bucket_has_a_label_to_render()
    {
        // The sidebar renders Label directly, so a bucket added later without one would render an empty
        // heading rather than fail a build.
        foreach (var bucket in Enum.GetValues<HistoryBucket>())
            Assert.False(string.IsNullOrWhiteSpace(ChatHistoryGrouping.LabelOf(bucket)));
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/SqlAgent.Tests/SqlAgent.Tests.csproj --filter ChatHistoryGroupingTests`
Expected: FAIL — compile error, `ChatHistoryGrouping` does not exist.

- [ ] **Step 3: Write the grouping**

Create `src/SqlAgent.Storage/ChatHistoryGrouping.cs`:

```csharp
namespace SqlAgent.Storage;

/// <summary>Which day-bucket a chat's last activity falls into.</summary>
public enum HistoryBucket { Today, Yesterday, Previous7Days, Previous30Days, Older }

/// <summary>One rendered section of the history list.</summary>
public record HistoryGroup(HistoryBucket Bucket, string Label, IReadOnlyList<ChatSummary> Chats);

/// <summary>
/// Groups history the way the sidebar shows it. Pure and clock-injected: the caller passes local "now",
/// so this is testable at 00:30 without waiting for 00:30.
///
/// Timestamps are stored in UTC and bucketed in LOCAL time. The host and the browser are the same
/// machine — the UI is loopback-only and single-user — so local time is genuinely the user's time here.
/// Day boundaries are calendar dates, not elapsed hours.
/// </summary>
public static class ChatHistoryGrouping
{
    public static HistoryBucket BucketOf(DateTime lastMessageAtUtc, DateTime nowLocal)
    {
        var days = (nowLocal.Date - lastMessageAtUtc.ToLocalTime().Date).Days;
        return days switch
        {
            // Negative means the stored timestamp is in the future — clock skew, a store copied from
            // another machine, or the timezone moving under a running host. Treated as Today so the
            // newest chat stays at the top instead of falling through to Older.
            <= 0 => HistoryBucket.Today,
            1 => HistoryBucket.Yesterday,
            <= 7 => HistoryBucket.Previous7Days,
            <= 30 => HistoryBucket.Previous30Days,
            _ => HistoryBucket.Older,
        };
    }

    public static string LabelOf(HistoryBucket bucket) => bucket switch
    {
        HistoryBucket.Today => "Today",
        HistoryBucket.Yesterday => "Yesterday",
        HistoryBucket.Previous7Days => "Previous 7 days",
        HistoryBucket.Previous30Days => "Previous 30 days",
        _ => "Older",
    };

    public static IReadOnlyList<HistoryGroup> Group(IEnumerable<ChatSummary> chats, DateTime nowLocal) =>
        chats
            .GroupBy(c => BucketOf(c.LastMessageAt, nowLocal))
            // Ordered by the enum value, not by contents: the members are declared newest-first, so this
            // is reading order, and a bucket with nothing in it simply never appears.
            .OrderBy(g => g.Key)
            .Select(g => new HistoryGroup(
                g.Key,
                LabelOf(g.Key),
                g.OrderByDescending(c => c.LastMessageAt).ToList()))
            .ToList();
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/SqlAgent.Tests/SqlAgent.Tests.csproj --filter ChatHistoryGroupingTests`
Expected: PASS, 12 tests (4 facts plus 8 theory cases).

- [ ] **Step 5: Commit**

```bash
git add src/SqlAgent.Storage/ChatHistoryGrouping.cs tests/SqlAgent.Tests/ChatHistoryGroupingTests.cs
git commit -m "$(cat <<'COMMIT'
Group chat history into day buckets, in local time

Calendar days, not elapsed hours: a chat from 23:00 last night is Yesterday when
you open the sidebar at 00:30, not Today. The clock is a parameter so the edges
are testable without waiting for midnight, and a future-dated timestamp (clock
skew, a store copied between machines) buckets as Today rather than falling
through to Older and burying the newest chat.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
COMMIT
)"
```

---

### Task 4: `ChatTurnService`

**Files:**
- Create: `src/SqlAgent.Storage/ChatTurnService.cs`
- Modify: `src/SqlAgent.Host/Program.cs` (register it)
- Test: `tests/SqlAgent.Tests/ChatTurnServiceTests.cs`

**Interfaces:**
- Consumes: `ChatService` (Task 2), the existing `NlQueryService` and `DatabaseConnectionService`.
- Produces:
  - `record ChatTurnResult(Guid ChatId, ChatMessageView UserMessage, ChatMessageView AssistantMessage, NlQueryResult? Live)` — `Live` carries the rows for the answer just produced and is never persisted.
  - `ChatTurnService.SendAsync(Guid? chatId, string question, IReadOnlyList<Guid> databaseIds, CancellationToken ct = default): Task<ChatTurnResult>`
  - `ChatTurnService.NoDatabaseAttached` = `"no_database_attached"`, `ChatTurnService.MultipleDatabasesUnsupported` = `"multiple_databases_unsupported"`.

- [ ] **Step 1: Write the failing test**

Create `tests/SqlAgent.Tests/ChatTurnServiceTests.cs`:

```csharp
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SqlAgent.Core;
using SqlAgent.Storage;

namespace SqlAgent.Tests;

/// <summary>
/// One turn, end to end, against the real NlQueryService over doubles. The invariant these exist to
/// protect is that the user's question reaches disk before anything can fail: a gateway that throws, a
/// dropped circuit, or a closed tab must never cost the typed text.
/// </summary>
public class ChatTurnServiceTests : IDisposable
{
    private readonly SqliteConnection _conn = new("DataSource=:memory:");
    private readonly SqlAgentDbContext _db;
    private readonly ChatService _chats;
    private readonly TurnGatewayStub _gateway = new();
    private readonly TurnProviderStub _provider = new();
    private readonly DatabaseConnectionService _connections;
    private readonly ChatTurnService _turns;

    public ChatTurnServiceTests()
    {
        _conn.Open();
        _db = new SqlAgentDbContext(
            new DbContextOptionsBuilder<SqlAgentDbContext>().UseSqlite(_conn).Options);
        _db.Database.EnsureCreated();

        var registry = new DatabaseProviderRegistry([_provider]);
        _connections = new DatabaseConnectionService(_db, new InMemorySecretStore());
        _chats = new ChatService(_db);
        var executor = new QueryExecutionService(_db, _connections, registry);
        var schemas = new SchemaService(_db, _connections, registry);
        _turns = new ChatTurnService(
            _chats, new NlQueryService(_connections, schemas, executor, _gateway), _connections);
    }

    [Fact]
    public async Task Sending_with_no_chat_creates_one_titled_after_the_question()
    {
        var id = await NewConnectionAsync("prod");

        var turn = await _turns.SendAsync(null, "how many orders", [id]);

        Assert.Equal("how many orders", (await _chats.GetChatAsync(turn.ChatId))!.Title);
    }

    [Fact]
    public async Task Sending_with_no_database_attached_answers_with_a_code_and_calls_nothing()
    {
        var turn = await _turns.SendAsync(null, "how many orders", []);

        Assert.Equal(ChatOutcomeKind.Error, turn.AssistantMessage.OutcomeKind);
        Assert.Equal("no_database_attached", turn.AssistantMessage.ErrorCode);
        Assert.Equal(0, _gateway.CallCount);
        // The question is still on disk with its own message, so the transcript is not a lone answer.
        var detail = await _chats.GetChatAsync(turn.ChatId);
        Assert.Equal([ChatRole.User, ChatRole.Assistant], detail!.Messages.Select(m => m.Role));
    }

    [Fact]
    public async Task Sending_with_two_databases_attached_says_so_rather_than_picking_one()
    {
        // Silently querying the first attachment would answer about a database the user did not single
        // out, with nothing in the transcript admitting the choice was made for them.
        var a = await NewConnectionAsync("a");
        var b = await NewConnectionAsync("b");

        var turn = await _turns.SendAsync(null, "compare them", [a, b]);

        Assert.Equal("multiple_databases_unsupported", turn.AssistantMessage.ErrorCode);
        Assert.Equal(0, _gateway.CallCount);
        // Both are recorded against the question, so the transcript shows what was asked of what.
        var user = (await _chats.GetChatAsync(turn.ChatId))!.Messages.First();
        Assert.Equal(["a", "b"], user.Databases.Select(d => d.Name).OrderBy(n => n));
    }

    [Fact]
    public async Task A_single_database_goes_through_the_ordinary_ask_path_and_persists_the_metadata()
    {
        var id = await NewConnectionAsync("prod");
        _gateway.NextResponse = LlmSqlResponse.Generated("SELECT id FROM orders");
        _provider.NextResult = new QueryResultSet(
            ["id"], [new object?[] { 1 }, new object?[] { 2 }], Truncated: true);

        var turn = await _turns.SendAsync(null, "orders", [id]);

        var answer = turn.AssistantMessage;
        Assert.Equal(ChatOutcomeKind.QueryResult, answer.OutcomeKind);
        Assert.Equal("SELECT id FROM orders", answer.GeneratedSql);
        Assert.Equal(2, answer.RowCount);
        Assert.True(answer.Truncated);
        // Rows come back on the live result for this render only.
        Assert.Equal(2, turn.Live!.Rows.Count);
    }

    [Fact]
    public async Task A_gateway_that_is_not_configured_still_leaves_a_question_and_an_answer_on_disk()
    {
        // Until the model service exists this is the ONLY path a real user takes, so it is the path that
        // proves persistence works at all. NotSupportedException is the documented "nothing is wired"
        // signal (see UnavailableLlmSqlGateway); NlQueryService maps it to llm_not_configured.
        var id = await NewConnectionAsync("prod");
        _gateway.Throw = new NotSupportedException("no provider");

        var turn = await _turns.SendAsync(null, "orders", [id]);

        Assert.Equal("llm_not_configured", turn.AssistantMessage.ErrorCode);
        var reloaded = await _chats.GetChatAsync(turn.ChatId);
        Assert.Equal(2, reloaded!.Messages.Count);
        Assert.Equal("orders", reloaded.Messages[0].Text);
        Assert.Equal("llm_not_configured", reloaded.Messages[1].ErrorCode);
    }

    [Fact]
    public async Task A_gateway_that_throws_anything_else_leaves_the_question_on_disk_too()
    {
        var id = await NewConnectionAsync("prod");
        _gateway.Throw = new HttpRequestException("connection reset");

        var turn = await _turns.SendAsync(null, "orders", [id]);

        Assert.Equal("llm_error", turn.AssistantMessage.ErrorCode);
        // The provider's own words never reach the transcript — they can echo a connection string.
        Assert.DoesNotContain("connection reset", turn.AssistantMessage.Text);
        Assert.Equal(2, (await _chats.GetChatAsync(turn.ChatId))!.Messages.Count);
    }

    [Fact]
    public async Task An_attached_database_that_was_deleted_is_recorded_by_name_with_no_id()
    {
        // Chips live in circuit state, and a connection can be deleted from another tab between
        // attaching and sending.
        var id = await NewConnectionAsync("prod");
        await _connections.DeleteAsync(id);

        var turn = await _turns.SendAsync(null, "orders", [id]);

        var user = (await _chats.GetChatAsync(turn.ChatId))!.Messages.First();
        var attached = Assert.Single(user.Databases);
        Assert.Null(attached.ConnectionId);
        Assert.Equal("(deleted database)", attached.Name);
        Assert.Equal("connection_not_found", turn.AssistantMessage.ErrorCode);
    }

    [Fact]
    public async Task An_empty_question_is_refused_before_anything_is_written()
    {
        // The composer disables Send for blank text, but Enter-to-send is a second entry point, so the
        // rule has to hold here too. Nothing is persisted: an empty user message would sit in the
        // transcript forever.
        var id = await NewConnectionAsync("prod");

        await Assert.ThrowsAsync<ArgumentException>(() => _turns.SendAsync(null, "   ", [id]));

        Assert.Empty(await _db.Chats.ToListAsync());
    }

    private async Task<Guid> NewConnectionAsync(string name) =>
        (await _connections.CreateAsync(
            new DatabaseConnectionInput(name, DatabaseProviderType.Postgres, IsReadOnly: true), "cs")).Id;

    public void Dispose()
    {
        _db.Dispose();
        _conn.Dispose();
    }
}

/// <summary>Gateway double: a canned response, or an exception thrown from the call.</summary>
sealed class TurnGatewayStub : ILlmSqlGateway
{
    public LlmSqlResponse? NextResponse { get; set; }
    public Exception? Throw { get; set; }
    public int CallCount { get; private set; }

    public Task<LlmSqlResponse> GenerateSqlAsync(LlmSqlRequest request, CancellationToken ct = default)
    {
        CallCount++;
        if (Throw is { } ex) throw ex;
        return Task.FromResult(NextResponse ?? LlmSqlResponse.Generated("SELECT 1"));
    }
}

/// <summary>Provider double returning a canned result set over an empty schema.</summary>
sealed class TurnProviderStub : IDatabaseProvider
{
    public QueryResultSet NextResult { get; set; } = new([], [], false);
    public DatabaseProviderType ProviderType => DatabaseProviderType.Postgres;

    public Task<ConnectionTestResult> TestConnectionAsync(string cs, CancellationToken ct = default)
        => Task.FromResult(ConnectionTestResult.Ok(null, 0));

    public Task<DatabaseSchema> GetSchemaAsync(string cs, CancellationToken ct = default)
        => Task.FromResult(new DatabaseSchema([]));

    public Task<QueryResultSet> ExecuteQueryAsync(
        string cs, string sql, QueryExecutionOptions o, CancellationToken ct = default)
        => Task.FromResult(NextResult);
}
```

If the `QueryExecutionService` or `SchemaService` constructor arguments above do not compile, match them
to the real signatures rather than changing those services: they are pre-existing and this task does not
touch them.

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/SqlAgent.Tests/SqlAgent.Tests.csproj --filter ChatTurnServiceTests`
Expected: FAIL — compile error, `ChatTurnService` does not exist.

- [ ] **Step 3: Write `ChatTurnService`**

Create `src/SqlAgent.Storage/ChatTurnService.cs`:

```csharp
namespace SqlAgent.Storage;

/// <summary>
/// One completed turn. <see cref="Live"/> is the in-memory result for the answer just produced,
/// including its rows — the page renders it once and it is never written anywhere. A reloaded
/// conversation has no Live for any message, which is why a restored answer shows metadata rather than
/// a grid.
/// </summary>
public record ChatTurnResult(
    Guid ChatId, ChatMessageView UserMessage, ChatMessageView AssistantMessage, NlQueryResult? Live);

/// <summary>
/// Runs one chat turn: persist the question, decide what the attached databases allow, ask, persist the
/// answer. Split out of <see cref="ChatService"/> so a whole turn is unit-testable without bUnit and the
/// page stays a thin caller.
///
/// The order is load-bearing. The user's message is written BEFORE the model is called, because
/// everything after that point can fail: a gateway that throws, a circuit that drops, a tab that closes.
/// Losing a typed question to any of those is the one outcome this design refuses.
/// </summary>
public class ChatTurnService(
    ChatService chats, NlQueryService nlQueries, DatabaseConnectionService connections)
{
    public const string NoDatabaseAttached = "no_database_attached";
    public const string MultipleDatabasesUnsupported = "multiple_databases_unsupported";

    /// <summary>Recorded for an attachment whose connection was deleted between attaching and sending.
    /// The chip carried a real name once; by send time there is nothing left to read it from, and
    /// inventing one would be worse than admitting the gap.</summary>
    private const string DeletedDatabaseName = "(deleted database)";

    public async Task<ChatTurnResult> SendAsync(
        Guid? chatId, string question, IReadOnlyList<Guid> databaseIds, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(question))
            throw new ArgumentException("A question is required.", nameof(question));

        // Names are resolved now, not at render time: this is the moment the snapshot is true.
        var attachments = new List<ChatDatabaseRef>(databaseIds.Count);
        foreach (var id in databaseIds)
        {
            var info = await connections.GetAsync(id, ct);
            attachments.Add(info is null
                ? new ChatDatabaseRef(null, DeletedDatabaseName)
                : new ChatDatabaseRef(info.Id, info.Name));
        }

        var chat = chatId ?? await chats.CreateChatAsync(ChatService.TitleFrom(question), ct);
        var userMessage = await chats.AppendMessageAsync(
            new ChatMessageInput(chat, ChatRole.User, question.Trim(), attachments), ct);

        var (live, answer) = await AnswerAsync(chat, question, databaseIds, ct);
        return new ChatTurnResult(chat, userMessage, answer, live);
    }

    private async Task<(NlQueryResult? Live, ChatMessageView Answer)> AnswerAsync(
        Guid chat, string question, IReadOnlyList<Guid> databaseIds, CancellationToken ct)
    {
        switch (databaseIds.Count)
        {
            case 0:
                return (null, await SaveErrorAsync(chat, NoDatabaseAttached,
                    "Attach a database from the composer's attachment menu, then ask again.", ct));

            case 1:
                // The ordinary path: NlQueryService applies policy, executes through the same
                // QueryExecutionService every other surface uses, and audits the run.
                var result = await nlQueries.AskAsync(databaseIds[0], question, ct);
                return (result, await chats.AppendMessageAsync(FromResult(chat, result), ct));

            default:
                // Today's gateway takes one schema and returns one SQL string. Querying the first
                // attachment and calling it an answer would misreport what was asked. The model-service
                // phase replaces this branch with a tool-calling loop.
                return (null, await SaveErrorAsync(chat, MultipleDatabasesUnsupported,
                    "One database at a time for now — detach the others and ask again.", ct));
        }
    }

    private Task<ChatMessageView> SaveErrorAsync(Guid chat, string code, string message, CancellationToken ct) =>
        chats.AppendMessageAsync(new ChatMessageInput(
            chat, ChatRole.Assistant, message, [], OutcomeKind: ChatOutcomeKind.Error, ErrorCode: code), ct);

    /// <summary>
    /// Maps an ask_database outcome onto a stored message. Failures are stored exactly like successes:
    /// dropping them would make a reloaded conversation shorter than the one the user watched, with
    /// their questions apparently unanswered.
    /// </summary>
    private static ChatMessageInput FromResult(Guid chat, NlQueryResult r) => r.Kind switch
    {
        NlResponseKind.QueryResult => new ChatMessageInput(
            chat, ChatRole.Assistant, "", [], r.GeneratedSql, ChatOutcomeKind.QueryResult,
            RowCount: r.RowCount, ElapsedMs: r.ElapsedMs, Truncated: r.Truncated),

        NlResponseKind.ClarificationRequired => new ChatMessageInput(
            chat, ChatRole.Assistant, r.ClarificationQuestion ?? "", [],
            OutcomeKind: ChatOutcomeKind.Clarification),

        _ => new ChatMessageInput(
            chat, ChatRole.Assistant, r.ErrorMessage ?? "", [], r.GeneratedSql, ChatOutcomeKind.Error,
            ErrorCode: r.ErrorCode, ElapsedMs: r.ElapsedMs == 0 ? null : r.ElapsedMs),
    };
}
```

- [ ] **Step 4: Register the service**

In `src/SqlAgent.Host/Program.cs`, beside `ChatService`:

```csharp
builder.Services.AddScoped<ChatTurnService>();
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/SqlAgent.Tests/SqlAgent.Tests.csproj --filter ChatTurnServiceTests`
Expected: PASS, 8 tests.

- [ ] **Step 6: Run the whole suite**

Run: `dotnet test SqlAgent.slnx --configuration Release`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add src/SqlAgent.Storage/ChatTurnService.cs src/SqlAgent.Host/Program.cs tests/SqlAgent.Tests/ChatTurnServiceTests.cs
git commit -m "$(cat <<'COMMIT'
Run one chat turn: persist, branch on the attached databases, persist again

The question is written before the model is called, because everything after that
point can fail — a gateway that throws, a dropped circuit, a closed tab. Answers
are persisted too, llm_not_configured included: dropping failures would make a
reloaded conversation shorter than the one the user watched.

Zero attached databases and two or more each answer with a stable code instead of
guessing. Today's gateway takes one schema and returns one SQL string, so
querying the first attachment would misreport what was asked; the tool-calling
loop that lifts the restriction belongs to the model-service phase.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
COMMIT
)"
```

---

### Task 5: Shell plumbing — icons, `AppState`, and a dialog host outside the sidebar

**Files:**
- Modify: `src/SqlAgent.Host/Components/Shared/Ui/Icon.razor`
- Modify: `src/SqlAgent.Host/Web/AppState.cs`
- Create: `src/SqlAgent.Host/Web/DialogService.cs`
- Create: `src/SqlAgent.Host/Components/Layout/DialogHost.razor` + `.razor.css`
- Modify: `src/SqlAgent.Host/Components/Layout/MainLayout.razor`
- Modify: `src/SqlAgent.Host/Program.cs` (register `DialogService`)
- Modify: `tests/SqlAgent.Tests/UiPrimitiveTests.cs` (the icon inventory)
- Test: `tests/SqlAgent.Tests/AppStateTests.cs`, `tests/SqlAgent.Tests/DialogHostTests.cs`

**Interfaces:**
- Consumes: `Icon`, `Modal` from Phase A.
- Produces:
  - Icon names `plus`, `terminal`, `paperclip`, `more-vertical`, `pencil`, `trash`, `arrow-up`, `square`.
  - `AppState.ActiveChatId: Guid?`, `AppState.SetActiveChat(Guid?)`, `AppState.ChatsChanged: event Action`, `AppState.NotifyChatsChanged()`.
  - `AppState.HandOffSql(string sql)`, `AppState.TakePendingSql(): string?`.
  - `DialogService.Current: RenderFragment?`, `DialogService.Changed: event Action`, `DialogService.Show(RenderFragment)`, `DialogService.Close()`.
  - `<DialogHost />`, rendered once by `MainLayout`.

- [ ] **Step 1: Write the failing tests**

Create `tests/SqlAgent.Tests/AppStateTests.cs`:

```csharp
using SqlAgent.Host.Web;

namespace SqlAgent.Tests;

/// <summary>
/// AppState exists because Blazor siblings do not re-render each other: the sidebar's history section
/// and the chat page are siblings under MainLayout, so a chat created on the page reaches the sidebar
/// only through an event. These pin the notification contract that makes that work.
/// </summary>
public class AppStateTests
{
    [Fact]
    public void Selecting_a_chat_announces_it_once()
    {
        var state = new AppState();
        var notified = 0;
        state.ChatsChanged += () => notified++;
        var id = Guid.NewGuid();

        state.SetActiveChat(id);

        Assert.Equal(id, state.ActiveChatId);
        Assert.Equal(1, notified);
    }

    [Fact]
    public void Re_selecting_the_same_chat_says_nothing()
    {
        // Every navigation to /chat/{id} sets the active chat, including a re-render of the page already
        // showing it. Announcing that would re-read the whole history list from SQLite for no change.
        var state = new AppState();
        var id = Guid.NewGuid();
        state.SetActiveChat(id);
        var notified = 0;
        state.ChatsChanged += () => notified++;

        state.SetActiveChat(id);

        Assert.Equal(0, notified);
    }

    [Fact]
    public void The_history_list_can_be_told_to_refresh_without_the_selection_moving()
    {
        // Rename and delete change the list while the selection stays put — the same distinction the
        // existing Changed/ConnectionsChanged pair already draws for connections.
        var state = new AppState();
        var id = Guid.NewGuid();
        state.SetActiveChat(id);
        var notified = 0;
        state.ChatsChanged += () => notified++;

        state.NotifyChatsChanged();

        Assert.Equal(1, notified);
        Assert.Equal(id, state.ActiveChatId);
    }

    [Fact]
    public void SQL_handed_to_the_editor_is_read_exactly_once()
    {
        // "Open in editor" sets this and navigates; /sql reads it on its first render. If the read did
        // not clear it, every later visit to /sql would silently overwrite whatever the user had typed
        // with the same old query.
        var state = new AppState();

        state.HandOffSql("SELECT 1");

        Assert.Equal("SELECT 1", state.TakePendingSql());
        Assert.Null(state.TakePendingSql());
    }
}
```

Create `tests/SqlAgent.Tests/DialogHostTests.cs`:

```csharp
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using SqlAgent.Host.Components.Layout;
using SqlAgent.Host.Web;

namespace SqlAgent.Tests;

/// <summary>
/// Dialogs are rendered from MainLayout rather than from wherever they are asked for, because below
/// 1024px the sidebar carries a CSS transform and a position:fixed descendant resolves against the
/// transformed element instead of the viewport (Phase A carry-forward item 1). A confirmation opened
/// from the history menu inside the drawer would centre on the drawer and ride off-screen with it.
/// </summary>
public class DialogHostTests
{
    [Fact]
    public void Nothing_renders_until_a_dialog_is_shown()
    {
        using var ctx = new Bunit.TestContext();
        var dialogs = new DialogService();
        ctx.Services.AddSingleton(dialogs);

        var host = ctx.RenderComponent<DialogHost>();

        Assert.Empty(host.Markup.Trim());
    }

    [Fact]
    public void A_shown_dialog_renders_and_a_closed_one_disappears()
    {
        using var ctx = new Bunit.TestContext();
        var dialogs = new DialogService();
        ctx.Services.AddSingleton(dialogs);
        var host = ctx.RenderComponent<DialogHost>();

        // Show is called from another component's event handler in real use, so the host is not the
        // component handling the event — it re-renders only because it subscribed. That is exactly the
        // failure mode this asserts against.
        host.InvokeAsync(() => dialogs.Show(b => b.AddMarkupContent(0, "<p id=\"d\">confirm?</p>")));
        Assert.Single(host.FindAll("#d"));

        host.InvokeAsync(dialogs.Close);
        Assert.Empty(host.FindAll("#d"));
    }

    [Fact]
    public void Showing_a_second_dialog_replaces_the_first()
    {
        // There is one host, so two dialogs would otherwise stack invisibly and the scrim of the second
        // would sit over the first with no way to reach it.
        using var ctx = new Bunit.TestContext();
        var dialogs = new DialogService();
        ctx.Services.AddSingleton(dialogs);
        var host = ctx.RenderComponent<DialogHost>();

        host.InvokeAsync(() => dialogs.Show(b => b.AddMarkupContent(0, "<p id=\"first\">a</p>")));
        host.InvokeAsync(() => dialogs.Show(b => b.AddMarkupContent(0, "<p id=\"second\">b</p>")));

        Assert.Empty(host.FindAll("#first"));
        Assert.Single(host.FindAll("#second"));
    }

    [Fact]
    public void Disposing_the_host_unsubscribes()
    {
        // The host lives in MainLayout for the whole circuit, but bUnit tears components down between
        // tests and a leaked handler would keep a disposed renderer alive — the same leak ShellTests
        // pins for Sidebar's LocationChanged subscription.
        using var ctx = new Bunit.TestContext();
        var dialogs = new DialogService();
        ctx.Services.AddSingleton(dialogs);
        ctx.RenderComponent<DialogHost>();

        ctx.DisposeComponents();

        // Show must not throw into a disposed renderer.
        dialogs.Show(b => b.AddMarkupContent(0, "<p>x</p>"));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/SqlAgent.Tests/SqlAgent.Tests.csproj --filter "AppStateTests|DialogHostTests"`
Expected: FAIL — compile errors, `DialogService`, `DialogHost`, `SetActiveChat`, `HandOffSql` do not exist.

- [ ] **Step 3: Add the eight glyphs**

In `src/SqlAgent.Host/Components/Shared/Ui/Icon.razor`, add to the `Paths` dictionary, keeping the
existing comment about each phase shipping only what it renders:

```csharp
        // Phase B1: New chat, the SQL page, the composer's attach/send/stop, and the history row menu.
        ["plus"] = ["M12 5 V19", "M5 12 H19"],
        ["terminal"] = [
            "M4 4 H20 A1 1 0 0 1 21 5 V19 A1 1 0 0 1 20 20 H4 A1 1 0 0 1 3 19 V5 A1 1 0 0 1 4 4 Z",
            "M7 9 L10 12 L7 15", "M12.5 15.5 H17"],
        ["paperclip"] = [
            "M20 11.5 L11.7 19.8 A4.5 4.5 0 0 1 5.3 13.4 L13.6 5.1 A3 3 0 0 1 17.9 9.4 L9.9 17.4 A1.5 1.5 0 0 1 7.8 15.3 L15 8.1"],
        // Three dots drawn as degenerate segments, the same trick the "info" glyph uses for its dot:
        // round line caps turn a zero-length stroke into a circle, so no per-icon fill flag is needed.
        ["more-vertical"] = ["M12 5.9 V6", "M12 11.9 V12", "M12 17.9 V18"],
        ["pencil"] = ["M4 20 V16 L16 4 A2.83 2.83 0 0 1 20 8 L8 20 Z", "M14 6 L18 10"],
        ["trash"] = [
            "M4 7 H20",
            "M9 7 V5 A1 1 0 0 1 10 4 H14 A1 1 0 0 1 15 5 V7",
            "M6 7 V19 A1 1 0 0 0 7 20 H17 A1 1 0 0 0 18 19 V7",
            "M10 11 V16", "M14 11 V16"],
        ["arrow-up"] = ["M12 20 V4", "M5 11 L12 4 L19 11"],
        ["square"] = ["M7 8 A1 1 0 0 1 8 7 H16 A1 1 0 0 1 17 8 V16 A1 1 0 0 1 16 17 H8 A1 1 0 0 1 7 16 Z"],
```

Then update the inventory in `tests/SqlAgent.Tests/UiPrimitiveTests.cs`. In
`No_icon_ships_that_nothing_renders`, extend the `rendered` array with the eight names above, and add
them to the `[InlineData]` list on `The_icons_the_shell_needs_all_exist`. Leave the comments explaining
the rule intact — Phase B1 adds these because Phase B1 draws them.

- [ ] **Step 4: Extend `AppState`**

Append to `src/SqlAgent.Host/Web/AppState.cs`, inside the class:

```csharp
    /// <summary>Which chat the page is showing, so the sidebar can highlight its row. Null on a new,
    /// unsaved chat — the row does not exist until the first message is sent.</summary>
    public Guid? ActiveChatId { get; private set; }

    /// <summary>
    /// Raised when the history list needs re-reading: a chat was created, renamed, deleted, or a message
    /// moved one to the top. Separate from the selection moving, for the same reason
    /// <see cref="ConnectionsChanged"/> is separate from <see cref="Changed"/> — the sidebar section is a
    /// sibling of the page, not a child, and nothing else would ever tell it.
    /// </summary>
    public event Action? ChatsChanged;

    public void SetActiveChat(Guid? chatId)
    {
        // Every render of /chat/{id} sets this, including re-renders of the chat already open. Firing on
        // an unchanged value would re-query the whole history list from SQLite for nothing.
        if (ActiveChatId == chatId) return;
        ActiveChatId = chatId;
        ChatsChanged?.Invoke();
    }

    /// <summary>Announces a create, rename, delete, or a new message, without moving the selection.</summary>
    public void NotifyChatsChanged() => ChatsChanged?.Invoke();

    private string? _pendingSql;

    /// <summary>Hands generated SQL to the /sql page across a navigation. The page and the chat are
    /// separate routes, so there is no parameter to pass it through.</summary>
    public void HandOffSql(string sql) => _pendingSql = sql;

    /// <summary>Reads the handed-off SQL and clears it. Clearing is the point: without it, every later
    /// visit to /sql would overwrite whatever the user had typed with the same stale query.</summary>
    public string? TakePendingSql()
    {
        var sql = _pendingSql;
        _pendingSql = null;
        return sql;
    }
```

- [ ] **Step 5: Write `DialogService`**

Create `src/SqlAgent.Host/Web/DialogService.cs`:

```csharp
using Microsoft.AspNetCore.Components;

namespace SqlAgent.Host.Web;

/// <summary>
/// The one dialog the circuit is currently showing.
///
/// It exists because of where dialogs get asked for. Below 1024px the sidebar is a drawer carrying a CSS
/// transform, and a transform makes the element a containing block for position:fixed descendants — so a
/// Modal rendered inside the sidebar centres on the drawer rather than the viewport, overhangs it, and
/// rides off-screen when the drawer closes while still considering itself open. Rendering every dialog
/// from MainLayout, outside that subtree, avoids the problem without a portal.
///
/// Scoped to the circuit, like AppState: one dialog per browser tab.
/// </summary>
public sealed class DialogService
{
    public RenderFragment? Current { get; private set; }

    /// <summary>Raised when <see cref="Current"/> changes. DialogHost subscribes; it is not the
    /// component handling the event that opened the dialog, so nothing else would re-render it.</summary>
    public event Action? Changed;

    /// <summary>Shows a dialog, replacing any dialog already open. One host means a second dialog would
    /// otherwise stack invisibly behind the first.</summary>
    public void Show(RenderFragment dialog)
    {
        Current = dialog;
        Changed?.Invoke();
    }

    public void Close()
    {
        Current = null;
        Changed?.Invoke();
    }
}
```

- [ ] **Step 6: Write `DialogHost`**

Create `src/SqlAgent.Host/Components/Layout/DialogHost.razor`:

```razor
@implements IDisposable
@inject DialogService Dialogs

@if (Dialogs.Current is { } dialog)
{
    @dialog
}

@code {
    protected override void OnInitialized() => Dialogs.Changed += OnChanged;

    // InvokeAsync because Show/Close are called from another component's handler, which may already be
    // on the renderer's synchronization context but is not guaranteed to be — the same shape SchemaRail
    // and Workspace use for their AppState subscriptions.
    private void OnChanged() => InvokeAsync(StateHasChanged);

    public void Dispose() => Dialogs.Changed -= OnChanged;
}
```

Create `src/SqlAgent.Host/Components/Layout/DialogHost.razor.css`:

```css
/* Nothing of its own: the dialog inside brings its own scrim and panel (Modal.razor.css). This file
   exists so the component has a stylesheet like every other one, and so a later phase has somewhere to
   put stacking rules if a second host is ever needed. */
```

Note: an empty stylesheet trips no test — `RestyleRegressionTests.Every_restyled_component_has_a_stylesheet`
enumerates specific paths and `DialogHost` is not among them. If the comment-only file is awkward in
review, delete it; nothing depends on it.

- [ ] **Step 7: Render it from `MainLayout` and register the service**

`src/SqlAgent.Host/Components/Layout/MainLayout.razor` becomes:

```razor
@inherits LayoutComponentBase

@* The header nav is gone: the sidebar owns navigation now. WorkArea stays exactly where it was —
   it is the error boundary for every page, and its LocationChanged recovery depends on living
   above @Body rather than inside a page.

   DialogHost is a sibling of the sidebar and of the page, deliberately. A dialog rendered inside the
   sidebar would resolve its position:fixed against the drawer's transform below 1024px; rendered inside
   the page it would vanish on navigation. Here it belongs to the layout, which outlives both. *@
<div class="app">
    <Sidebar />
    <main class="app-main">
        <div class="app-card custom-scroll">
            <WorkArea>@Body</WorkArea>
        </div>
    </main>
    <DialogHost />
</div>
```

In `src/SqlAgent.Host/Program.cs`, beside `AppState`:

```csharp
builder.Services.AddScoped<DialogService>();
```

- [ ] **Step 8: Run the tests to verify they pass**

Run: `dotnet test tests/SqlAgent.Tests/SqlAgent.Tests.csproj --filter "AppStateTests|DialogHostTests|UiPrimitiveTests"`
Expected: PASS.

- [ ] **Step 9: Run the whole suite**

Run: `dotnet test SqlAgent.slnx --configuration Release`
Expected: PASS.

- [ ] **Step 10: Commit**

```bash
git add src/SqlAgent.Host tests/SqlAgent.Tests/AppStateTests.cs tests/SqlAgent.Tests/DialogHostTests.cs tests/SqlAgent.Tests/UiPrimitiveTests.cs
git commit -m "$(cat <<'COMMIT'
Add the shell plumbing Phase B1 needs: glyphs, chat state, a dialog host

DialogHost renders every dialog from MainLayout rather than from wherever it was
asked for. Below 1024px the sidebar carries a CSS transform, which makes it the
containing block for position:fixed descendants — a confirmation opened from the
history menu inside the drawer would centre on the drawer and ride off-screen
with it. This is Phase A carry-forward item 1, and B1 is where it stops being
theoretical.

AppState gains the active chat and a ChatsChanged event, because the sidebar's
history section and the chat page are siblings and siblings do not re-render each
other, plus a one-shot handoff slot for SQL sent from an answer to the editor.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
COMMIT
)"
```

---

### Task 6: Sidebar hardening — collapsed-rule parity, and a drawer that leaves the tab order

**Files:**
- Modify: `src/SqlAgent.Host/wwwroot/css/app.css:288-298`
- Modify: `src/SqlAgent.Host/Components/Layout/Sidebar.razor.css:42-51`
- Modify: `src/SqlAgent.Host/Components/Layout/Sidebar.razor`
- Modify: `tests/SqlAgent.Tests/ShellTests.cs` (two source-text assertions follow the scoping change)
- Test: `tests/SqlAgent.Tests/SidebarCollapseParityTests.cs`

**Interfaces:**
- Consumes: nothing new.
- Produces: no new API. `Sidebar` gains a `_trigger` element reference used to restore focus.

This task closes Phase A carry-forward items 7 and 3, and it comes **before** the history section
because both defects get worse the moment the sidebar grows another section.

- [ ] **Step 1: Write the failing test**

Create `tests/SqlAgent.Tests/SidebarCollapseParityTests.cs`:

```csharp
using System.Text.RegularExpressions;

namespace SqlAgent.Tests;

/// <summary>
/// The collapsed sidebar is described twice, on purpose, and Phase A left the two copies agreeing only
/// by hand.
///
/// app.css keys on html.sidebar-collapsed, which theme.js sets before first paint — that is the only
/// collapsed styling in effect until the circuit connects, or forever if it never does.
/// Sidebar.razor.css keys on the scoped .collapsed class the component adds after reading the browser
/// back. Both are needed; neither can replace the other. What was missing is anything that fails when
/// they drift, and drift here shows up as a sidebar that renders one width and then snaps to another —
/// the exact defect the pre-paint rule was added to fix.
///
/// bUnit runs no CSS engine, so this compares the two rule sets as source text, the same way
/// DesignSystemTests pins the two dark palettes to the same property set.
/// </summary>
public class SidebarCollapseParityTests
{
    [Fact]
    public void The_pre_paint_and_circuit_collapsed_rules_style_the_same_targets_the_same_way()
    {
        var prePaint = PrePaintRules();
        var circuit = CircuitRules();

        Assert.NotEmpty(prePaint);
        Assert.Equal(circuit.Keys.OrderBy(k => k, StringComparer.Ordinal),
                     prePaint.Keys.OrderBy(k => k, StringComparer.Ordinal));
        foreach (var (target, declarations) in circuit)
            Assert.Equal(declarations, prePaint[target]);
    }

    [Fact]
    public void The_pre_paint_rules_cannot_reach_outside_the_sidebar()
    {
        // "html.sidebar-collapsed .nav-label" hides every .nav-label on the page, not just the ones in
        // the sidebar. Nothing else uses that class today, which is exactly why an unscoped selector
        // would sit here undetected until something did.
        var css = File.ReadAllText(RepoPaths.Find("src/SqlAgent.Host/wwwroot/css/app.css"));

        foreach (var selector in Selectors(css).Where(s => s.Contains("html.sidebar-collapsed")))
            Assert.Contains(".app aside.sidebar", selector);
    }

    /// <summary>Target-to-declarations for the wide-viewport collapsed rules in app.css, with the
    /// "html.sidebar-collapsed .app aside.sidebar" prefix stripped so the two sheets compare.</summary>
    private static Dictionary<string, string> PrePaintRules()
    {
        var css = File.ReadAllText(RepoPaths.Find("src/SqlAgent.Host/wwwroot/css/app.css"));
        return Normalize(RulesIn(css, "html.sidebar-collapsed"),
            ["html.sidebar-collapsed .app aside.sidebar", "html.sidebar-collapsed"]);
    }

    /// <summary>The same, for the rules that apply once the circuit has added the scoped class: the
    /// unconditional ".sidebar.collapsed" rule plus everything inside the wide-viewport media query. The
    /// narrow block is deliberately excluded — it undoes the collapse for the drawer, and the pre-paint
    /// sheet has no counterpart because it is guarded to wide viewports too.</summary>
    private static Dictionary<string, string> CircuitRules()
    {
        var css = File.ReadAllText(RepoPaths.Find("src/SqlAgent.Host/Components/Layout/Sidebar.razor.css"));
        var narrow = Block(css, "@media (max-width: 1023px)");
        var withoutNarrow = css.Replace(narrow, "");
        return Normalize(RulesIn(withoutNarrow, ".sidebar.collapsed"), [".sidebar.collapsed", "::deep"]);
    }

    /// <summary>Every "selector { declarations }" pair whose selector mentions <paramref name="marker"/>.</summary>
    private static List<(string Selector, string Declarations)> RulesIn(string css, string marker) =>
        Regex.Matches(StripComments(css), @"([^{}]+)\{([^{}]*)\}")
            .Select(m => (Selector: m.Groups[1].Value.Trim(), Declarations: m.Groups[2].Value))
            .Where(r => r.Selector.Contains(marker, StringComparison.Ordinal))
            .ToList();

    /// <summary>Reduces each rule to (what it targets, what it sets). Selector lists are split, the
    /// sheet-specific prefixes are removed, and declarations are sorted so ordering is not a difference.
    /// A target appearing in more than one rule accumulates: the scoped sheet sets width and flex-basis
    /// unconditionally and overflow inside the media query, while app.css sets all three at once.</summary>
    private static Dictionary<string, string> Normalize(
        List<(string Selector, string Declarations)> rules, string[] prefixes)
    {
        var byTarget = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (selector, declarations) in rules)
        {
            foreach (var one in selector.Split(','))
            {
                var target = one.Trim();
                foreach (var prefix in prefixes) target = target.Replace(prefix, "");
                target = target.Trim();
                if (target.Length == 0) target = ".sidebar";

                if (!byTarget.TryGetValue(target, out var list)) byTarget[target] = list = [];
                list.AddRange(declarations
                    .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            }
        }
        return byTarget.ToDictionary(
            kv => kv.Key,
            kv => string.Join("; ", kv.Value.OrderBy(d => d, StringComparer.Ordinal)),
            StringComparer.Ordinal);
    }

    private static IEnumerable<string> Selectors(string css) =>
        Regex.Matches(StripComments(css), @"([^{}]+)\{[^{}]*\}").Select(m => m.Groups[1].Value.Trim());

    // Comments in both sheets discuss these selectors at length; matching on prose would compare
    // documentation instead of rules.
    private static string StripComments(string css) =>
        Regex.Replace(css, @"/\*.*?\*/", "", RegexOptions.Singleline);

    private static string Block(string css, string selector)
    {
        var start = css.IndexOf(selector, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Expected to find '{selector}'.");
        var open = css.IndexOf('{', start);
        var depth = 0;
        var i = open;
        for (; i < css.Length; i++)
        {
            if (css[i] == '{') depth++;
            else if (css[i] == '}' && --depth == 0) break;
        }
        return css[start..(i + 1)];
    }
}
```

Add to `tests/SqlAgent.Tests/ShellTests.cs`:

```csharp
    [Fact]
    public void A_closed_drawer_is_out_of_the_tab_order_below_1024px()
    {
        // transform: translateX(-100%) moves the drawer off-screen but leaves every control in it
        // focusable, so Tab on a phone walks through an invisible sidebar before reaching the page —
        // pre-existing since Phase A, and worse now that history rows live in there too. visibility:
        // hidden is what actually removes a subtree from the tab order, and unlike an inert attribute it
        // can be scoped to the viewport where the drawer exists: above 1024px the sidebar is permanent
        // and must stay tabbable. bUnit runs no CSS engine, so this is pinned on source text.
        var css = File.ReadAllText(RepoPaths.Find("src/SqlAgent.Host/Components/Layout/Sidebar.razor.css"));

        var narrow = ExtractBlock(css, "@media (max-width: 1023px)");
        Assert.Contains("visibility: hidden", narrow);
        Assert.Contains(".sidebar.drawer-open", narrow);
        Assert.Contains("visibility: visible", narrow);
    }

    [Fact]
    public void Closing_the_drawer_returns_focus_to_the_hamburger_that_opened_it()
    {
        // Opening the drawer moves focus into it (Phase A). Closing it without giving focus back leaves
        // the focus ring on an element that is now hidden, so the next Tab restarts from the top of the
        // document — the classic dialog-dismissal defect, and the other half of carry-forward item 3.
        var sidebar = _ctx.RenderComponent<Sidebar>();
        sidebar.Find("[data-testid=drawer-open]").Click();
        var afterOpen = FocusInvocationCount();

        sidebar.Find(".sidebar-scrim").Click();

        Assert.True(FocusInvocationCount() > afterOpen,
            "Closing the drawer must move focus back to the trigger.");
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/SqlAgent.Tests/SqlAgent.Tests.csproj --filter "SidebarCollapseParityTests|ShellTests"`
Expected: FAIL — the parity test finds unscoped `html.sidebar-collapsed .nav-label` selectors, the
visibility test finds no `visibility` in the narrow block, and the focus test sees no second focus call.

- [ ] **Step 3: Scope the pre-paint rules under the sidebar**

In `src/SqlAgent.Host/wwwroot/css/app.css`, replace the selector list inside the `@media (min-width: 1024px)`
block (lines 294-297) so every rule is anchored to the sidebar. Keep the existing explanatory comment
above the block and append a sentence to it:

```css
  html.sidebar-collapsed .app aside.sidebar .nav-label,
  html.sidebar-collapsed .app aside.sidebar .brand-name,
  html.sidebar-collapsed .app aside.sidebar .sidebar-body,
  html.sidebar-collapsed .app aside.sidebar .sidebar-foot { display: none; }
```

Comment to append:

```
   Every selector here is anchored to ".app aside.sidebar" rather than left as a bare class. The class
   names happen to be unique to the sidebar today, so "html.sidebar-collapsed .nav-label" behaved
   correctly — but it says "hide every .nav-label on the page", which is not the rule anyone means, and
   SidebarCollapseParityTests now fails if a new one is added unanchored.
```

- [ ] **Step 4: Take the closed drawer out of the tab order and restore focus**

In `src/SqlAgent.Host/Components/Layout/Sidebar.razor.css`, extend the base `.sidebar` transition and the
narrow block:

```css
.sidebar {
  display: flex;
  flex-direction: column;
  width: var(--sidebar-width);
  flex: 0 0 var(--sidebar-width);
  padding: var(--space-5);
  background: var(--background-50);
  border-right: 1px solid var(--base-100);
  /* visibility is in the list so the closed drawer stays visible for the length of the slide-out rather
     than blinking away at frame one; visibility interpolates discretely, so it flips at the end. */
  transition: width .25s, flex-basis .25s, transform .25s, visibility .25s;
}
```

```css
@media (max-width: 1023px) {
  .sidebar {
    position: fixed;
    inset: 0 auto 0 0;
    z-index: 50;
    transform: translateX(-100%);
    /* transform alone moves the drawer off-screen but leaves everything in it focusable, so Tab walks
       an invisible sidebar before reaching the page. visibility: hidden is what actually removes a
       subtree from the tab order, and scoping it to this media query keeps the permanent sidebar above
       1024px tabbable. */
    visibility: hidden;
  }
  .sidebar.drawer-open { transform: translateX(0); visibility: visible; }
  .sidebar.collapsed { width: var(--sidebar-width); flex-basis: var(--sidebar-width); }
}
```

In `src/SqlAgent.Host/Components/Layout/Sidebar.razor`, give the hamburger a reference and return focus
to it on close. Replace the trigger button and `CloseDrawer`:

```razor
<button type="button" class="ghost drawer-trigger" data-testid="drawer-open" @ref="_trigger"
        @onclick="OpenDrawer" aria-label="Open menu">
    <Icon Name="menu" Size="20" />
</button>
```

```csharp
    private ElementReference _trigger;
    private bool _focusTriggerPending;

    /// <summary>
    /// Closes the drawer and hands focus back to the hamburger that opened it. Without the second half,
    /// focus is left on an element the closing drawer has just made visibility:hidden, and the browser
    /// restarts the tab order from the top of the document — the same defect an unrestored dialog leaves
    /// behind. The move is deferred to OnAfterRenderAsync for the same reason opening one is: the class
    /// that hides the drawer is applied by the render this handler triggers.
    /// </summary>
    private void CloseDrawer()
    {
        if (!_drawerOpen) return;
        _drawerOpen = false;
        _focusTriggerPending = true;
    }
```

and extend the existing post-render focus step:

```csharp
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender)
        {
            await FocusDrawerIfJustOpenedAsync();
            await FocusTriggerIfJustClosedAsync();
            return;
        }
        // ... unchanged first-render body ...
    }

    /// <summary>The mirror of FocusDrawerIfJustOpenedAsync. Same three interop failures, same reasoning
    /// about which are fatal to the circuit and which only cost evidence — see ToggleCollapseAsync.</summary>
    private async Task FocusTriggerIfJustClosedAsync()
    {
        if (!_focusTriggerPending) return;
        _focusTriggerPending = false;
        try
        {
            await _trigger.FocusAsync();
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException or OperationCanceledException)
        {
            Logger.Log(ex is JSException ? LogLevel.Warning : LogLevel.Debug, ex,
                "Returning focus to the drawer trigger failed; the tab order restarts from the document top.");
        }
    }
```

`OnLocationChanged` sets `_drawerOpen = false` directly today. Route it through `CloseDrawer()` so a
navigation restores focus too:

```csharp
    private void OnLocationChanged(object? sender, LocationChangedEventArgs e) =>
        InvokeAsync(() => { CloseDrawer(); StateHasChanged(); });
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/SqlAgent.Tests/SqlAgent.Tests.csproj --filter "SidebarCollapseParityTests|ShellTests"`
Expected: PASS. `The_collapsed_hiding_rules_are_guarded_to_wide_viewports` and
`The_app_stylesheet_reads_back_the_pre_paint_collapsed_class` still pass unchanged — the scoping change
only lengthens selectors those tests match on by substring.

If `The_app_stylesheet_reads_back_the_pre_paint_collapsed_class` fails, it is asserting
`html.sidebar-collapsed .sidebar-body` as a literal substring; update those two assertions to the newly
anchored selectors and say so in the commit message.

- [ ] **Step 6: Run the whole suite**

Run: `dotnet test SqlAgent.slnx --configuration Release`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add src/SqlAgent.Host tests/SqlAgent.Tests
git commit -m "$(cat <<'COMMIT'
Pin the two collapsed-sidebar rule sets together, and free the closed drawer

The collapsed sidebar is styled twice on purpose — app.css keys on the class
theme.js sets before first paint, Sidebar.razor.css on the scoped class the
circuit adds — and Phase A left the copies agreeing only by hand. Drift shows up
as a sidebar that renders one width and snaps to another, which is the defect the
pre-paint rule was added to fix. A parity test now compares both rule sets as
source text, and the app.css selectors are anchored under .app aside.sidebar
instead of hiding every .nav-label on the page.

The closed drawer also kept its contents in the tab order below 1024px: transform
moves it off-screen but leaves it focusable, so Tab walked an invisible sidebar
before reaching the page. visibility: hidden inside the narrow media query fixes
that without touching the permanent sidebar above it, and closing the drawer now
hands focus back to the hamburger instead of stranding it on a hidden element.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
COMMIT
)"
```

---

### Task 7: `Workspace` loses its tabs and moves to `/sql`

**Files:**
- Modify: `src/SqlAgent.Host/Components/Pages/Workspace.razor`
- Modify: `src/SqlAgent.Host/Components/Pages/Workspace.razor.css`
- Modify: `src/SqlAgent.Host/Components/Layout/SidebarNav.razor`
- Rename: `tests/SqlAgent.Tests/WorkspaceChatTests.cs` → `tests/SqlAgent.Tests/ChatOutcomeTests.cs`
- Create: `tests/SqlAgent.Tests/ChatGatewayStub.cs`
- Modify: `tests/SqlAgent.Tests/ShellTests.cs`, `tests/SqlAgent.Tests/RestyleRegressionTests.cs`

**Interfaces:**
- Consumes: `AppState.TakePendingSql()` from Task 5.
- Produces: route `/sql`. `Workspace` **keeps `@page "/"` as well**, temporarily, and Task 9 deletes
  that line when `Chat.razor` claims the root.

  This was a correction made during execution. The plan originally left `/` with no page until Task 9,
  on the assumption that Blazor's `<NotFound>` fallback would answer the root with a 200 and the
  integration tests that fetch `/` would pass. It does not: `MapRazorComponents` registers endpoints
  only for discovered `@page` routes, so with no component claiming `/` the request 404s at routing and
  the router never runs. Five `TokenAuthTests` / `DesignSystemTests` cases failed. Keeping the existing
  page on its existing route two tasks longer costs one line that Task 9 removes by construction, and
  it keeps the suite green at every commit — which is what lets the next task tell its own breakage
  from inherited breakage.

- [ ] **Step 1: Move the chat tests out of the way**

`WorkspaceChatTests.cs` mixes two kinds of test. The five that render `ChatOutcome` directly are about a
component this task does not touch; the five that drive the chat tab through `Workspace` are about a tab
this task deletes, and their coverage moves to `ChatPageTests` in Task 9.

1. Rename the file to `tests/SqlAgent.Tests/ChatOutcomeTests.cs` and the class to `ChatOutcomeTests`.
2. Keep only the five component tests: `An_llm_not_configured_error_is_explained_rather_than_shown_as_a_raw_code`,
   `A_genuine_llm_error_does_not_get_the_not_configured_explanation`,
   `A_clarification_shows_the_question_and_no_sql`, `A_rejected_query_still_shows_the_generated_sql`,
   `A_successful_answer_shows_the_generated_sql_and_the_rows`. They construct their own `TestContext`
   and need none of the fixture, so the constructor, the fields, `IDisposable`, and the private helpers
   all go with the deleted tests.
3. Delete the six that render `Workspace`: `Selecting_the_Chat_tab_with_no_connection_selected_shows_the_prompt_not_the_transcript`,
   `Whitespace_only_question_keeps_the_Ask_button_disabled`,
   `Two_sequential_questions_each_keep_their_own_question_paired_with_their_own_result`,
   `A_second_Ask_click_while_the_first_is_in_flight_is_ignored`,
   `Switching_to_the_SQL_tab_while_a_question_is_in_flight_does_not_lose_the_answer`,
   and `Open_in_editor_switches_to_the_SQL_tab_and_seeds_a_freshly_mounted_editor_with_the_generated_sql`.
   Task 9 re-establishes each behaviour against the chat page; nothing is silently dropped.
4. Move `ChatGatewayStub` verbatim into its own file, `tests/SqlAgent.Tests/ChatGatewayStub.cs`, keeping
   its XML comment. Task 9's page tests need its `Hold()`/`Release()` control.

Add to the top of `ChatOutcomeTests.cs`:

```csharp
/// <summary>
/// ChatOutcome in isolation. The tab that used to wrap it is gone — Phase B1 moved conversations to
/// Chat.razor and the SQL editor to /sql — but the component itself survives until Phase D replaces it
/// with SqlBlock and DataTable, and these five outcomes are exactly what it has to keep rendering.
/// </summary>
```

- [ ] **Step 2: Run the suite to see what the move left behind**

Run: `dotnet test SqlAgent.slnx --configuration Release`
Expected: PASS. Nothing has changed in `src/` yet; this only confirms the test split is clean before the
page changes underneath it.

- [ ] **Step 3: Rewrite `Workspace.razor`**

```razor
@* Temporary, removed in Task 9: the root route stays here until Chat.razor claims it, so the suite is
   green at every commit rather than only at the end of the phase. MapRazorComponents registers
   endpoints only for discovered @page routes, so a root with no component 404s at routing — the
   router's NotFound never runs, and every integration test that fetches "/" fails. *@
@page "/"
@page "/sql"
@implements IDisposable
@inject ScopedRunner Runner
@inject AppState State

@* The tab strip is gone. Conversations live on Chat.razor (/), and this page is what it always was
   underneath: a plain SQL editor over the same validated execution path. It is not a temporary parking
   spot — a full-screen editor is the right shape for a long query and a wide result, and Phase D's
   ScratchPad panel on the chat page renders these same components rather than replacing them. *@
<h1>SQL</h1>

@if (State.ConnectionId is null)
{
    <p>Select a connection to start querying.</p>
}
else
{
    <SqlEditor @bind-Value="_sql" OnRun="RunAsync" />

    <div class="actions">
        <button @onclick="RunAsync" disabled="@(_running || string.IsNullOrWhiteSpace(_sql))">Run</button>
        @if (_running)
        {
            <button @onclick="Cancel">Cancel</button>
        }
    </div>

    <ResultGrid Result="_result" />
}

@code {
    private string _sql = "";
    private bool _running;
    private QueryExecutionResult? _result;
    private CancellationTokenSource? _cts;

    // SchemaRail and this page are siblings under MainLayout (not parent/child), so a connection picked
    // from the rail's dropdown does not, by itself, cause this page to re-render — only the rail's own
    // subtree does. Subscribing here is what makes selecting a connection update this pane immediately.
    //
    // TakePendingSql is the other half of "open in editor" on the chat page: the two are separate
    // routes, so there is no parameter to pass the SQL through, and the read clears it so a later visit
    // does not overwrite whatever the user has typed with the same stale query.
    protected override void OnInitialized()
    {
        State.Changed += OnStateChanged;
        _sql = State.TakePendingSql() ?? "";
    }

    private void OnStateChanged() => InvokeAsync(StateHasChanged);

    public void Dispose() => State.Changed -= OnStateChanged;

    private async Task RunAsync()
    {
        if (State.ConnectionId is not { } id) return;
        // The Run button's disabled attribute is not the only way in: SqlEditor's Ctrl+Enter (OnRun)
        // calls this directly, trivially reachable via OS key repeat while a query is already running or
        // over blank SQL. Guarding here makes both entry points obey the same rule.
        if (_running || string.IsNullOrWhiteSpace(_sql)) return;
        _running = true;
        _result = null;
        _cts = new CancellationTokenSource();
        try
        {
            // ExecuteSqlAsync validates policy, enforces the timeout and row cap, and writes the audit
            // row itself. Cancelling here surfaces as execution_canceled, distinct from execution_timeout.
            _result = await Runner.RunAsync<QueryExecutionService, QueryExecutionResult>(
                s => s.ExecuteSqlAsync(id, _sql, _cts.Token));
        }
        finally
        {
            _running = false;
            _cts.Dispose();
            _cts = null;
        }
    }

    private void Cancel() => _cts?.Cancel();
}
```

- [ ] **Step 4: Delete the dead rules from `Workspace.razor.css`**

Remove the `.tabs`, `.tabs button`, `.tabs button:hover`, `.tabs button.active`, `.transcript` and
`.question` rules together with the comment block that explains the transcript's flex layout. None of
that markup exists on this page any more; Task 9 gives the chat components their own stylesheets.

Then, in `tests/SqlAgent.Tests/RestyleRegressionTests.cs`, remove `"tabs"`, `"transcript"` and
`"question"` from `ClassesUsedByExistingMarkup` and extend the array's leading comment:

```csharp
    // "tabs", "transcript" and "question" left with Phase B1: the tab strip is gone and the transcript
    // moved to Components/Shared/Chat, which brings its own stylesheets and its own assertion.
```

- [ ] **Step 5: Update the sidebar's navigation**

`src/SqlAgent.Host/Components/Layout/SidebarNav.razor`:

```razor
@* Search joins these in Phase B2, with the modal behind it. A row that navigates nowhere is worse than
   no row, which is why Phase A shipped only the routes that existed. *@
<nav class="sidebar-nav">
    <NavLink class="nav-row" href="/" Match="NavLinkMatch.All">
        <Icon Name="plus" Size="18" />
        <span class="nav-label">New chat</span>
    </NavLink>
    <NavLink class="nav-row" href="/sql">
        <Icon Name="terminal" Size="18" />
        <span class="nav-label">SQL</span>
    </NavLink>
    <NavLink class="nav-row" href="/connections">
        <Icon Name="database" Size="18" />
        <span class="nav-label">Connections</span>
    </NavLink>
    <NavLink class="nav-row" href="/settings">
        <Icon Name="settings" Size="18" />
        <span class="nav-label">Settings</span>
    </NavLink>
</nav>

@code {
    [Parameter] public bool Collapsed { get; set; }
}
```

In `tests/SqlAgent.Tests/ShellTests.cs`, `The_sidebar_renders_the_product_mark_and_the_routes_that_exist`
asserts on the old row names. Replace the two assertions and the stale comment:

```csharp
        Assert.Contains("SQL Agent", sidebar.Markup);
        // Phase B1 replaced the Workspace row: conversations are the front door now, and the SQL editor
        // keeps its own row rather than a tab inside a page. Search arrives in B2 with its modal.
        Assert.Contains("New chat", sidebar.Markup);
        Assert.Contains("Connections", sidebar.Markup);
```

- [ ] **Step 6: Run the suite**

Run: `dotnet test SqlAgent.slnx --configuration Release`
Expected: PASS, the whole suite. `WorkspaceTests` renders `Workspace` directly rather than through the
router and never touched the tab strip, so all eleven of its tests stay green unchanged, and the
temporary `@page "/"` keeps every integration test that fetches the root passing.

If you are tempted to drop that root route to "finish the move" — don't. Task 9 removes it in the same
step that adds `Chat.razor`, which is the only moment at which something else answers `/`.

- [ ] **Step 7: Commit**

```bash
git add src/SqlAgent.Host tests/SqlAgent.Tests
git commit -m "$(cat <<'COMMIT'
Give the SQL editor its own page and free the root route

Workspace was two tabs in a trench coat: a SQL editor and a chat transcript that
lived only until reload. The editor is the half worth keeping, so it becomes a
page of its own at /sql, permanently — a long query and a wide result want a full
screen, and Phase D's ScratchPad will render the same components beside the chat
rather than replacing this.

The chat half goes with the tab strip. Its five ChatOutcome component tests move
to ChatOutcomeTests; the five that drove the tab through Workspace are replaced
by the chat page's own tests in the next task. Nothing at / until then.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
COMMIT
)"
```

---

### Task 8: The composer, its chips, and the attachment menu

**Files:**
- Create: `src/SqlAgent.Host/Components/Shared/Chat/AttachmentChips.razor` + `.razor.css`
- Create: `src/SqlAgent.Host/Components/Shared/Chat/AttachmentMenu.razor` + `.razor.css`
- Create: `src/SqlAgent.Host/Components/Shared/Chat/Composer.razor` + `.razor.css`
- Create: `src/SqlAgent.Host/wwwroot/js/composer.js`
- Modify: `src/SqlAgent.Host/Components/App.razor` (load `composer.js`)
- Modify: `src/SqlAgent.Host/Components/_Imports.razor`
- Test: `tests/SqlAgent.Tests/ComposerTests.cs`

**Interfaces:**
- Consumes: `ChatDatabaseRef` (Task 2), `Menu`/`MenuItem`/`Icon`/`Spinner`/`EmptyState` from Phase A, `DatabaseConnectionInfo`.
- Produces:
  - `<AttachmentChips Databases="IReadOnlyList<ChatDatabaseRef>" OnRemove="EventCallback<ChatDatabaseRef>" ReadOnly="bool" />` — `ReadOnly` drops the `×` for chips rendered inside a sent message.
  - `<AttachmentMenu Connections="IReadOnlyList<DatabaseConnectionInfo>" AttachedIds="IReadOnlyList<Guid>" OnAttach="EventCallback<DatabaseConnectionInfo>" />`
  - `<Composer Value="string" ValueChanged="EventCallback<string>" Attached="IReadOnlyList<ChatDatabaseRef>" OnRemoveAttachment="EventCallback<ChatDatabaseRef>" Tools="RenderFragment?" Busy="bool" OnSend="EventCallback" OnStop="EventCallback" />` — `Tools`, not `Menu`: a child element named `<Menu>` inside `<Composer>` would read as the `Menu` UI component and resolve as a parameter only by luck of nesting.
  - `Composer.SendFromEditor()` — `[JSInvokable]`, called by `composer.js` on Enter without Shift.
  - `window.sqlAgentComposer.bind(textarea, dotNetRef)` and `window.sqlAgentComposer.unbind(textarea)`.

- [ ] **Step 1: Write the failing test**

Create `tests/SqlAgent.Tests/ComposerTests.cs`:

```csharp
using Bunit;
using Microsoft.AspNetCore.Components;
using SqlAgent.Core;
using SqlAgent.Host.Components.Shared.Chat;
using SqlAgent.Storage;

namespace SqlAgent.Tests;

public class ComposerTests
{
    private static Bunit.TestContext NewContext()
    {
        var ctx = new Bunit.TestContext();
        // Composer binds its Enter handling in JS, the same shape SqlEditor uses for Ctrl+Enter. bUnit's
        // strict JSInterop needs both calls planned; the `_ => true` matcher accepts any arguments
        // because one of them is an ElementReference the test cannot predict.
        ctx.JSInterop.SetupVoid("sqlAgentComposer.bind", _ => true);
        ctx.JSInterop.SetupVoid("sqlAgentComposer.unbind", _ => true);
        return ctx;
    }

    private static IReadOnlyList<DatabaseConnectionInfo> TwoConnections() =>
    [
        new(Guid.NewGuid(), "analytics", DatabaseProviderType.Postgres, true, true, DateTime.UtcNow, DateTime.UtcNow),
        new(Guid.NewGuid(), "billing", DatabaseProviderType.SqlServer, false, true, DateTime.UtcNow, DateTime.UtcNow),
    ];

    [Fact]
    public void The_attachment_menu_lists_saved_connections_by_name()
    {
        // By name, because the name is what identifies a database everywhere else the agent is used —
        // the MCP tools address them the same way. An id in this list would be meaningless to the user.
        using var ctx = NewContext();
        var connections = TwoConnections();

        var menu = ctx.RenderComponent<AttachmentMenu>(p => p.Add(m => m.Connections, connections));
        menu.Find(".menu-trigger").Click();

        Assert.Contains("analytics", menu.Markup);
        Assert.Contains("billing", menu.Markup);
    }

    [Fact]
    public void Choosing_a_database_reports_it_and_an_already_attached_one_cannot_be_chosen_twice()
    {
        // A second row for a database already in the chips would either duplicate the attachment or
        // silently do nothing; both read as a broken menu.
        using var ctx = NewContext();
        var connections = TwoConnections();
        DatabaseConnectionInfo? chosen = null;

        var menu = ctx.RenderComponent<AttachmentMenu>(p => p
            .Add(m => m.Connections, connections)
            .Add(m => m.AttachedIds, new[] { connections[0].Id })
            .Add(m => m.OnAttach, EventCallback.Factory.Create<DatabaseConnectionInfo>(
                new object(), c => chosen = c)));
        menu.Find(".menu-trigger").Click();

        var rows = menu.FindAll(".menu-item-action");
        Assert.Single(rows);
        rows[0].Click();

        Assert.Equal("billing", chosen!.Name);
    }

    [Fact]
    public void With_no_connections_saved_the_menu_offers_the_way_to_make_one()
    {
        // The empty state is the whole content of the menu here. Without it the popover opens onto
        // nothing and reads as a bug rather than as "you have not set up a database yet".
        using var ctx = NewContext();

        var menu = ctx.RenderComponent<AttachmentMenu>(p => p.Add(m => m.Connections, Array.Empty<DatabaseConnectionInfo>()));
        menu.Find(".menu-trigger").Click();

        Assert.Contains("No databases", menu.Markup);
        Assert.Equal("/connections", menu.Find(".empty a").GetAttribute("href"));
    }

    [Fact]
    public void Chips_render_one_per_attached_database_and_removing_one_reports_it()
    {
        using var ctx = NewContext();
        var removed = default(ChatDatabaseRef);
        var attached = new List<ChatDatabaseRef>
        {
            new(Guid.NewGuid(), "analytics"),
            new(Guid.NewGuid(), "billing"),
        };

        var chips = ctx.RenderComponent<AttachmentChips>(p => p
            .Add(c => c.Databases, attached)
            .Add(c => c.OnRemove, EventCallback.Factory.Create<ChatDatabaseRef>(
                new object(), d => removed = d)));

        Assert.Equal(2, chips.FindAll(".chip").Count);
        chips.FindAll(".chip-remove")[1].Click();

        Assert.Equal("billing", removed!.Name);
    }

    [Fact]
    public void Chips_on_a_sent_message_carry_no_remove_button()
    {
        // A sent message's attachments are history. Offering an × on them would imply the record can be
        // edited after the fact.
        using var ctx = NewContext();

        var chips = ctx.RenderComponent<AttachmentChips>(p => p
            .Add(c => c.Databases, new List<ChatDatabaseRef> { new(Guid.NewGuid(), "analytics") })
            .Add(c => c.ReadOnly, true));

        Assert.Single(chips.FindAll(".chip"));
        Assert.Empty(chips.FindAll(".chip-remove"));
    }

    [Fact]
    public void Send_is_disabled_for_blank_text_and_reports_the_question_otherwise()
    {
        using var ctx = NewContext();
        var sends = 0;

        var composer = ctx.RenderComponent<Composer>(p => p
            .Add(c => c.Value, "   ")
            .Add(c => c.OnSend, EventCallback.Factory.Create(new object(), () => sends++)));

        Assert.True(composer.Find("[data-testid=send]").HasAttribute("disabled"));

        composer.SetParametersAndRender(p => p.Add(c => c.Value, "how many orders"));
        composer.Find("[data-testid=send]").Click();

        Assert.Equal(1, sends);
    }

    [Fact]
    public async Task Enter_without_shift_sends_through_the_same_path_as_the_button()
    {
        // composer.js calls this [JSInvokable] on Enter, exactly as sql-editor.js calls SqlEditor's
        // RunFromEditor on Ctrl+Enter. Invoking it directly drives the identical path a real keypress
        // would, without a JS engine to press the key.
        using var ctx = NewContext();
        var sends = 0;
        var composer = ctx.RenderComponent<Composer>(p => p
            .Add(c => c.Value, "how many orders")
            .Add(c => c.OnSend, EventCallback.Factory.Create(new object(), () => sends++)));

        await composer.InvokeAsync(() => composer.Instance.SendFromEditor());

        Assert.Equal(1, sends);
    }

    [Fact]
    public async Task Enter_on_blank_text_or_while_busy_sends_nothing()
    {
        // The button carries a disabled attribute; the key handler bypasses it entirely, so the rule has
        // to live in the component. Same shape as WorkspaceTests' Ctrl+Enter guards on the SQL page.
        using var ctx = NewContext();
        var sends = 0;
        var composer = ctx.RenderComponent<Composer>(p => p
            .Add(c => c.Value, "   ")
            .Add(c => c.OnSend, EventCallback.Factory.Create(new object(), () => sends++)));

        await composer.InvokeAsync(() => composer.Instance.SendFromEditor());
        Assert.Equal(0, sends);

        composer.SetParametersAndRender(p => p
            .Add(c => c.Value, "how many orders")
            .Add(c => c.Busy, true));
        await composer.InvokeAsync(() => composer.Instance.SendFromEditor());

        Assert.Equal(0, sends);
    }

    [Fact]
    public void While_a_question_is_in_flight_send_becomes_stop()
    {
        // The same cancellation the SQL page has always offered, in the place a chat user looks for it.
        using var ctx = NewContext();
        var stops = 0;

        var composer = ctx.RenderComponent<Composer>(p => p
            .Add(c => c.Value, "how many orders")
            .Add(c => c.Busy, true)
            .Add(c => c.OnStop, EventCallback.Factory.Create(new object(), () => stops++)));

        Assert.Empty(composer.FindAll("[data-testid=send]"));
        composer.Find("[data-testid=stop]").Click();

        Assert.Equal(1, stops);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/SqlAgent.Tests/SqlAgent.Tests.csproj --filter ComposerTests`
Expected: FAIL — compile error, `SqlAgent.Host.Components.Shared.Chat` does not exist.

- [ ] **Step 3: Write `AttachmentChips`**

Create `src/SqlAgent.Host/Components/Shared/Chat/AttachmentChips.razor`:

```razor
@if (Databases.Count > 0)
{
    <div class="chips">
        @foreach (var database in Databases)
        {
            <span class="chip" data-database="@database.Name">
                <Icon Name="database" Size="14" />
                <span class="chip-name truncate">@database.Name</span>
                @if (!ReadOnly)
                {
                    <button type="button" class="ghost chip-remove"
                            @onclick="() => OnRemove.InvokeAsync(database)"
                            aria-label="@($"Detach {database.Name}")">
                        <Icon Name="x" Size="12" />
                    </button>
                }
            </span>
        }
    </div>
}

@code {
    [Parameter] public IReadOnlyList<ChatDatabaseRef> Databases { get; set; } = [];
    [Parameter] public EventCallback<ChatDatabaseRef> OnRemove { get; set; }

    /// <summary>True for chips under a message that has already been sent. Those attachments are a
    /// record, not a choice, so they carry no remove button.</summary>
    [Parameter] public bool ReadOnly { get; set; }
}
```

Create `src/SqlAgent.Host/Components/Shared/Chat/AttachmentChips.razor.css`:

```css
.chips { display: flex; flex-wrap: wrap; gap: var(--space-2); }

.chip {
  display: inline-flex;
  align-items: center;
  gap: var(--space-1);
  max-width: 220px;
  padding: 2px var(--space-2);
  border: 1px solid var(--base-200);
  border-radius: var(--radius-pill);
  background: var(--background-soft-100);
  color: var(--text-50);
  font-size: var(--text-xs);
}

/* Overrides the base button padding from app.css: a 12px glyph inside a pill cannot carry the
   8px/12px a full-size control does. */
.chip-remove { padding: 0; line-height: 0; }
```

- [ ] **Step 4: Write `AttachmentMenu`**

Create `src/SqlAgent.Host/Components/Shared/Chat/AttachmentMenu.razor`:

```razor
@* Phase E adds a Files section beside this one. The section heading exists from the start so that
   addition is a second block rather than a restructuring. *@
<Menu Placement="MenuPlacement.Top">
    <Trigger>
        <Icon Name="paperclip" Size="18" />
        <span class="sr-only">Attach a database</span>
    </Trigger>
    <ChildContent>
        <p class="menu-section">Databases</p>
        @if (Selectable.Count == 0)
        {
            <EmptyState Icon="database"
                        Title="@(Connections.Count == 0 ? "No databases yet" : "All databases attached")"
                        Hint="@(Connections.Count == 0 ? "Add a connection to ask questions about it." : null)">
                @if (Connections.Count == 0)
                {
                    <a href="/connections">Add a connection</a>
                }
            </EmptyState>
        }
        else
        {
            @foreach (var connection in Selectable)
            {
                <MenuItem Icon="database" OnClick="() => OnAttach.InvokeAsync(connection)">
                    @connection.Name
                </MenuItem>
            }
        }
    </ChildContent>
</Menu>

@code {
    [Parameter] public IReadOnlyList<DatabaseConnectionInfo> Connections { get; set; } = [];

    /// <summary>What the composer already carries. Those rows are left out rather than shown and
    /// ignored: a row that does nothing when clicked reads as a broken menu.</summary>
    [Parameter] public IReadOnlyList<Guid> AttachedIds { get; set; } = [];

    [Parameter] public EventCallback<DatabaseConnectionInfo> OnAttach { get; set; }

    private IReadOnlyList<DatabaseConnectionInfo> Selectable =>
        Connections.Where(c => !AttachedIds.Contains(c.Id)).ToList();
}
```

Create `src/SqlAgent.Host/Components/Shared/Chat/AttachmentMenu.razor.css`:

```css
.menu-section {
  padding: var(--space-1) var(--space-3);
  color: var(--text-100);
  font-size: var(--text-xs);
  font-weight: 500;
  text-transform: uppercase;
  letter-spacing: .04em;
}
```

- [ ] **Step 5: Write `composer.js`**

Create `src/SqlAgent.Host/wwwroot/js/composer.js`:

```js
// Enter-to-send and auto-grow, in JS for the same reason sql-editor.js exists: neither is expressible
// in Blazor alone. @onkeydown:preventDefault is evaluated when the element renders, not per keystroke,
// so it cannot tell Enter from Shift+Enter — and without preventDefault the browser inserts the newline
// before the handler can stop it, sending a question with a stray line break on the end.
window.sqlAgentComposer = {
  bind: function (textarea, dotNetRef) {
    if (!textarea) return;

    const onKeyDown = function (e) {
      // Shift+Enter is a newline, as in every chat composer. IME composition (Japanese, Chinese,
      // Korean) also raises Enter to accept a candidate; isComposing tells that apart from a send.
      if (e.key !== 'Enter' || e.shiftKey || e.isComposing) return;
      e.preventDefault();
      dotNetRef.invokeMethodAsync('SendFromEditor');
    };

    const onInput = function () {
      // Auto-grow to content, capped at 40vh so a pasted essay cannot push the transcript off screen.
      textarea.style.height = 'auto';
      const cap = window.innerHeight * 0.4;
      textarea.style.height = Math.min(textarea.scrollHeight, cap) + 'px';
    };

    textarea.addEventListener('keydown', onKeyDown);
    textarea.addEventListener('input', onInput);
    // Kept on the element so unbind can remove exactly these listeners; a component that is disposed
    // and re-created (navigating between chats) must not leave the old ones attached to a dead
    // DotNetObjectReference.
    textarea._sqlAgentComposer = { onKeyDown: onKeyDown, onInput: onInput };
    onInput();
  },

  unbind: function (textarea) {
    const handlers = textarea && textarea._sqlAgentComposer;
    if (!handlers) return;
    textarea.removeEventListener('keydown', handlers.onKeyDown);
    textarea.removeEventListener('input', handlers.onInput);
    delete textarea._sqlAgentComposer;
  },
};
```

In `src/SqlAgent.Host/Components/App.razor`, add it beside the other body scripts, before
`blazor.web.js`:

```razor
    <script src="js/composer.js"></script>
```

- [ ] **Step 6: Write `Composer`**

Create `src/SqlAgent.Host/Components/Shared/Chat/Composer.razor`:

```razor
@implements IAsyncDisposable
@inject IJSRuntime JS
@inject ILogger<Composer> Logger

<div class="composer">
    <AttachmentChips Databases="Attached" OnRemove="OnRemoveAttachment" />

    <textarea @ref="_textarea" class="composer-input custom-scroll" rows="1"
              placeholder="Ask a question about your data"
              value="@Value"
              @oninput="OnInput"></textarea>

    <div class="composer-actions">
        <div class="composer-tools">@Tools</div>

        @if (Busy)
        {
            <Spinner Size="14" Label="Waiting for an answer" />
            <button type="button" class="composer-send" data-testid="stop"
                    @onclick="OnStop" aria-label="Stop">
                <Icon Name="square" Size="16" />
            </button>
        }
        else
        {
            <button type="button" class="composer-send primary" data-testid="send"
                    @onclick="OnSend" disabled="@(!CanSend)" aria-label="Send">
                <Icon Name="arrow-up" Size="16" />
            </button>
        }
    </div>
</div>

@code {
    [Parameter] public string Value { get; set; } = "";
    [Parameter] public EventCallback<string> ValueChanged { get; set; }
    [Parameter] public IReadOnlyList<ChatDatabaseRef> Attached { get; set; } = [];
    [Parameter] public EventCallback<ChatDatabaseRef> OnRemoveAttachment { get; set; }

    /// <summary>The attachment menu, supplied by the page. The composer does not read connections
    /// itself: it is a leaf component with no service dependencies, which is what keeps it testable
    /// without a database. Named Tools rather than Menu because a child element named &lt;Menu&gt;
    /// inside &lt;Composer&gt; also names the Menu UI component, and one of the two readings is a
    /// silent surprise.</summary>
    [Parameter] public RenderFragment? Tools { get; set; }

    [Parameter] public bool Busy { get; set; }
    [Parameter] public EventCallback OnSend { get; set; }
    [Parameter] public EventCallback OnStop { get; set; }

    private ElementReference _textarea;
    private DotNetObjectReference<Composer>? _self;

    private bool CanSend => !Busy && !string.IsNullOrWhiteSpace(Value);

    private Task OnInput(ChangeEventArgs e) => ValueChanged.InvokeAsync(e.Value?.ToString() ?? "");

    /// <summary>
    /// Enter without Shift, routed from composer.js. The button's disabled attribute cannot gate this —
    /// a key handler bypasses it entirely — so the same rule is checked here, exactly as the SQL page
    /// re-checks its own guard for Ctrl+Enter.
    /// </summary>
    [JSInvokable]
    public async Task SendFromEditor()
    {
        if (!CanSend) return;
        await OnSend.InvokeAsync();
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender) return;
        _self = DotNetObjectReference.Create(this);
        try
        {
            await JS.InvokeVoidAsync("sqlAgentComposer.bind", _textarea, _self);
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException or OperationCanceledException)
        {
            // Losing the binding costs Enter-to-send and auto-grow; the send button still works, so
            // there is nothing to recover and nothing worth telling the user. Level split by meaning,
            // as everywhere else in this project: a JSException means composer.js is missing or threw,
            // which is a real bug worth clearing the Information default.
            Logger.Log(ex is JSException ? LogLevel.Warning : LogLevel.Debug, ex,
                "sqlAgentComposer.bind failed; Enter will insert a newline instead of sending.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await JS.InvokeVoidAsync("sqlAgentComposer.unbind", _textarea);
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException or OperationCanceledException)
        {
            // Teardown during a circuit that is already gone is the ordinary case here, not a fault.
            Logger.Log(ex is JSException ? LogLevel.Warning : LogLevel.Debug, ex,
                "sqlAgentComposer.unbind failed.");
        }
        _self?.Dispose();
    }
}
```

Create `src/SqlAgent.Host/Components/Shared/Chat/Composer.razor.css`:

```css
.composer {
  display: flex;
  flex-direction: column;
  gap: var(--space-2);
  padding: var(--space-3);
  border: 1px solid var(--base-200);
  border-radius: var(--radius-card);
  background: var(--input-background);
}
.composer:focus-within { border-color: var(--primary-300); }

/* No border or background of its own: the wrapper above is the visible control, and a second box
   inside it reads as an input nested in an input. */
.composer-input {
  border: none;
  background: none;
  padding: 0;
  resize: none;
  min-height: 24px;
  max-height: 40vh;
  color: var(--title-50);
  font: inherit;
}
.composer-input:focus { outline: none; }

.composer-actions { display: flex; align-items: center; justify-content: space-between; gap: var(--space-2); }
.composer-tools { display: flex; align-items: center; gap: var(--space-1); }

.composer-send {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 32px;
  height: 32px;
  padding: 0;
  border-radius: var(--radius-round);
}
```

- [ ] **Step 7: Make the namespace available**

In `src/SqlAgent.Host/Components/_Imports.razor`, append:

```razor
@using SqlAgent.Host.Components.Shared.Chat
```

- [ ] **Step 8: Run the tests to verify they pass**

Run: `dotnet test tests/SqlAgent.Tests/SqlAgent.Tests.csproj --filter ComposerTests`
Expected: PASS, 9 tests.

- [ ] **Step 9: Run the whole suite**

Run: `dotnet test SqlAgent.slnx --configuration Release`
Expected: PASS.

- [ ] **Step 10: Commit**

```bash
git add src/SqlAgent.Host tests/SqlAgent.Tests/ComposerTests.cs
git commit -m "$(cat <<'COMMIT'
Add the composer, its database chips, and the attachment menu

Databases are attached to a message from the same menu Phase E will add files to,
and they are listed by the name they carry everywhere else the agent is used —
the MCP tools address them the same way. A database already attached is left out
of the menu rather than shown and ignored.

Enter-to-send is JS, like SqlEditor's Ctrl+Enter, because Blazor evaluates
@onkeydown:preventDefault when the element renders rather than per keystroke and
so cannot tell Enter from Shift+Enter; without preventDefault the newline is in
the textarea before any handler could stop it. The [JSInvokable] re-checks the
same guard the send button's disabled attribute carries, since a key handler
bypasses that attribute entirely.

Spinner and EmptyState finally have callers, closing Phase A carry-forward 9.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
COMMIT
)"
```

---

### Task 9: The chat page

**Files:**
- Create: `src/SqlAgent.Host/Components/Shared/Chat/UserMessage.razor` + `.razor.css`
- Create: `src/SqlAgent.Host/Components/Shared/Chat/AssistantMessage.razor` + `.razor.css`
- Create: `src/SqlAgent.Host/Components/Shared/Chat/MessageList.razor` + `.razor.css`
- Create: `src/SqlAgent.Host/Components/Pages/Chat.razor` + `.razor.css`
- Modify: `src/SqlAgent.Host/Components/Shared/ChatOutcome.razor` + `.razor.css` (a `Restored` parameter)
- Modify: `src/SqlAgent.Host/Components/Shared/Chat/Composer.razor` (expose `FocusAsync`)
- Modify: `src/SqlAgent.Host/Components/Pages/Workspace.razor` (delete its temporary `@page "/"`)
- Test: `tests/SqlAgent.Tests/ChatPageTests.cs`, `tests/SqlAgent.Tests/ChatOutcomeTests.cs` (one new fact)

**Interfaces:**
- Consumes: `ChatService`, `ChatTurnService`, `AppState`, `Composer`, `AttachmentMenu`, `AttachmentChips`.
- Produces:
  - `<UserMessage Message="ChatMessageView" />`
  - `<AssistantMessage Message="ChatMessageView" Live="NlQueryResult?" OnOpenInEditor="EventCallback<string>" />`
  - `<MessageList Messages="IReadOnlyList<ChatMessageView>" Live="IReadOnlyDictionary<Guid, NlQueryResult>" OnOpenInEditor="EventCallback<string>" />`
  - `<ChatOutcome ... Restored="bool" />`
  - `Composer.FocusAsync(): Task`
  - Routes `/` and `/chat/{Id:guid}`.

- [ ] **Step 1: Write the failing test**

Create `tests/SqlAgent.Tests/ChatPageTests.cs`:

```csharp
using Bunit;
using Bunit.TestDoubles;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SqlAgent.Core;
using SqlAgent.Host.Components.Pages;
using SqlAgent.Host.Components.Shared.Chat;
using SqlAgent.Host.Web;
using SqlAgent.Storage;
using static SqlAgent.Tests.AsyncTestHelpers;

namespace SqlAgent.Tests;

/// <summary>
/// The chat page wired to the real ChatTurnService and NlQueryService over an in-memory store, the same
/// shape WorkspaceTests uses for the SQL page. These re-establish the behaviours the deleted
/// WorkspaceChatTests covered for the old chat tab — a blank question, a second send while one is in
/// flight, open-in-editor — and add the ones only persistence makes possible.
/// </summary>
public class ChatPageTests : IDisposable
{
    private readonly SqliteConnection _conn = new("DataSource=:memory:");
    private readonly Bunit.TestContext _ctx = new();
    private readonly ChatGatewayStub _gateway = new();
    private readonly TurnProviderStub _provider = new();

    public ChatPageTests()
    {
        _conn.Open();
        _ctx.Services.AddDbContext<SqlAgentDbContext>(o => o.UseSqlite(_conn));
        _ctx.Services.AddSingleton<ISecretStore, InMemorySecretStore>();
        _ctx.Services.AddSingleton<IDatabaseProvider>(_provider);
        _ctx.Services.AddSingleton<IDatabaseProviderRegistry, DatabaseProviderRegistry>();
        _ctx.Services.AddSingleton<ILlmSqlGateway>(_gateway);
        _ctx.Services.AddScoped<DatabaseConnectionService>();
        _ctx.Services.AddScoped<QueryExecutionService>();
        _ctx.Services.AddScoped<SchemaService>();
        _ctx.Services.AddScoped<NlQueryService>();
        _ctx.Services.AddScoped<ChatService>();
        _ctx.Services.AddScoped<ChatTurnService>();
        _ctx.Services.AddScoped<ScopedRunner>();
        _ctx.Services.AddScoped<AppState>();
        _ctx.Services.AddScoped<DialogService>();

        using var scope = _ctx.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<SqlAgentDbContext>().Database.EnsureCreated();

        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
    }

    [Fact]
    public void A_new_chat_offers_the_composer_and_suggestions_rather_than_an_empty_transcript()
    {
        var page = _ctx.RenderComponent<Chat>();

        Assert.Contains("How can I help with your data?", page.Markup);
        Assert.NotEmpty(page.FindAll("[data-testid=suggestion]"));
        Assert.Empty(page.FindAll(".message"));
    }

    [Fact]
    public void A_suggestion_fills_the_composer_instead_of_sending()
    {
        // With no model configured, a chip that sent immediately would answer every click with an error
        // panel. Filling the box lets the question be edited first, which is the point of a suggestion.
        var page = _ctx.RenderComponent<Chat>();

        page.FindAll("[data-testid=suggestion]")[0].Click();

        Assert.Equal(0, _gateway.CallCount);
        Assert.NotEmpty(page.Find("textarea").GetAttribute("value")!);
    }

    [Fact]
    public async Task Sending_the_first_message_creates_the_chat_and_moves_to_its_route()
    {
        var connection = await AddConnectionAsync("prod");
        var page = _ctx.RenderComponent<Chat>();
        await AttachAsync(page, "prod");
        Type(page, "how many orders");

        await ClickAsync(page.Find("[data-testid=send]"));

        var chat = Assert.Single(await ListChatsAsync());
        Assert.Equal("how many orders", chat.Title);
        var nav = _ctx.Services.GetRequiredService<FakeNavigationManager>();
        Assert.EndsWith($"/chat/{chat.Id}", nav.Uri);

        // The attachment reached the store as a snapshot: the live id, and the name it had at send time.
        var question = (await LoadAsync(chat.Id))!.Messages.First();
        var attached = Assert.Single(question.Databases);
        Assert.Equal(connection, attached.ConnectionId);
        Assert.Equal("prod", attached.Name);
    }

    [Fact]
    public void Opening_a_new_chat_and_leaving_without_sending_creates_no_row()
    {
        // The single most visible way a chat app accumulates junk. The row is written on first send, not
        // on first render.
        _ctx.RenderComponent<Chat>();
        _ctx.DisposeComponents();

        Assert.Empty(ListChatsAsync().GetAwaiter().GetResult());
    }

    [Fact]
    public async Task A_reloaded_chat_shows_its_messages_its_snapshot_and_no_grid()
    {
        // The whole phase in one test: what a user sees after pressing F5. Rows are gone by design, so
        // the answer must show its metadata rather than an empty table pretending the query returned
        // nothing.
        await AddConnectionAsync("prod");
        _gateway.NextResponse = LlmSqlResponse.Generated("SELECT id FROM orders");
        _provider.NextResult = new QueryResultSet(["id"], [new object?[] { 1 }], Truncated: false);

        var page = _ctx.RenderComponent<Chat>();
        await AttachAsync(page, "prod");
        Type(page, "how many orders");
        await ClickAsync(page.Find("[data-testid=send]"));
        var chatId = (await ListChatsAsync()).Single().Id;

        // A second component instance for the same route is what a reload actually is.
        var reloaded = _ctx.RenderComponent<Chat>(p => p.Add(c => c.Id, chatId));

        Assert.Contains("how many orders", reloaded.Markup);
        Assert.Contains("SELECT id FROM orders", reloaded.Markup);
        Assert.Contains("prod", reloaded.Markup);
        Assert.Contains("Rows are not stored", reloaded.Markup);
        Assert.Empty(reloaded.FindAll(".grid-scroll table tbody tr"));
    }

    [Fact]
    public async Task Sending_with_nothing_attached_explains_itself_and_keeps_the_question()
    {
        var page = _ctx.RenderComponent<Chat>();
        Type(page, "how many orders");

        await ClickAsync(page.Find("[data-testid=send]"));

        Assert.Equal(0, _gateway.CallCount);
        Assert.Contains("attachment menu", page.Markup);
        Assert.Contains("how many orders", page.Markup);
    }

    [Fact]
    public async Task Chips_stay_attached_for_the_next_question()
    {
        // Attachments are per message, but re-attaching on every turn would make a ten-question
        // conversation ten attachments. The chips carry over until they are removed.
        await AddConnectionAsync("prod");
        var page = _ctx.RenderComponent<Chat>();
        await AttachAsync(page, "prod");
        Type(page, "first");
        await ClickAsync(page.Find("[data-testid=send]"));

        Assert.Contains(page.FindAll(".composer .chip"), c => c.TextContent.Contains("prod"));
    }

    [Fact]
    public async Task A_second_send_while_one_is_in_flight_is_ignored()
    {
        await AddConnectionAsync("prod");
        _gateway.Hold();
        var page = _ctx.RenderComponent<Chat>();
        await AttachAsync(page, "prod");
        Type(page, "first question");

        var first = ClickAsync(page.Find("[data-testid=send]"));
        await WaitForConditionAsync(() => _gateway.CallCount == 1);

        // The send button is a stop button now, so there is nothing to click twice — which is the
        // guard. Driving the key path instead proves it holds there too.
        Assert.Empty(page.FindAll("[data-testid=send]"));
        await page.InvokeAsync(() => page.FindComponent<Composer>().Instance.SendFromEditor());

        Assert.Equal(1, _gateway.CallCount);

        _gateway.Release(LlmSqlResponse.Generated("SELECT 1"));
        await first;
    }

    [Fact]
    public async Task Open_in_editor_hands_the_sql_to_the_sql_page()
    {
        // The two are separate routes now, so the handoff goes through AppState rather than a tab flip.
        await AddConnectionAsync("prod");
        _gateway.NextResponse = LlmSqlResponse.Generated("SELECT id FROM orders");
        _provider.NextResult = new QueryResultSet(["id"], [new object?[] { 1 }], false);

        var page = _ctx.RenderComponent<Chat>();
        await AttachAsync(page, "prod");
        Type(page, "orders");
        await ClickAsync(page.Find("[data-testid=send]"));

        await ClickAsync(page.FindAll("button").First(b => b.TextContent.Trim() == "Open in editor"));

        var nav = _ctx.Services.GetRequiredService<FakeNavigationManager>();
        Assert.EndsWith("/sql", nav.Uri);
        Assert.Equal("SELECT id FROM orders", _ctx.Services.GetRequiredService<AppState>().TakePendingSql());
    }

    [Fact]
    public async Task The_page_tells_the_sidebar_that_history_changed()
    {
        // The history section is a sibling under MainLayout, so nothing else would ever tell it a chat
        // was created.
        await AddConnectionAsync("prod");
        var notified = 0;
        _ctx.Services.GetRequiredService<AppState>().ChatsChanged += () => notified++;

        var page = _ctx.RenderComponent<Chat>();
        await AttachAsync(page, "prod");
        Type(page, "orders");
        await ClickAsync(page.Find("[data-testid=send]"));

        Assert.True(notified > 0);
    }

    private async Task<Guid> AddConnectionAsync(string name)
    {
        using var scope = _ctx.Services.CreateScope();
        var connections = scope.ServiceProvider.GetRequiredService<DatabaseConnectionService>();
        var created = await connections.CreateAsync(
            new DatabaseConnectionInput(name, DatabaseProviderType.Postgres, IsReadOnly: true), "cs");
        return created.Id;
    }

    private async Task<IReadOnlyList<ChatSummary>> ListChatsAsync()
    {
        using var scope = _ctx.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ChatService>().ListHistoryAsync();
    }

    private async Task<ChatDetail?> LoadAsync(Guid id)
    {
        using var scope = _ctx.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ChatService>().GetChatAsync(id);
    }

    /// <summary>Opens the attachment menu and picks a database by name, the way a user does.</summary>
    private static async Task AttachAsync(IRenderedComponent<Chat> page, string name)
    {
        await ClickAsync(page.Find(".composer .menu-trigger"));
        await ClickAsync(page.FindAll(".composer .menu-item-action")
            .First(r => r.TextContent.Contains(name)));
    }

    private static void Type(IRenderedComponent<Chat> page, string text) =>
        page.Find("textarea").Input(text);

    private static Task ClickAsync(AngleSharp.Dom.IElement element) =>
        element.ClickAsync(new MouseEventArgs());

    public void Dispose()
    {
        _ctx.Dispose();
        _conn.Dispose();
    }
}
```

Add one fact to `tests/SqlAgent.Tests/ChatOutcomeTests.cs`:

```csharp
    [Fact]
    public void A_restored_answer_shows_its_numbers_and_says_where_the_rows_went()
    {
        // A reloaded QueryResult has no rows — they are never stored. Rendering the usual table would
        // draw an empty grid, which reads as "the query returned nothing" rather than "the rows are not
        // kept". This is the only visible consequence of the rows-not-persisted rule, so it says so.
        using var ctx = new Bunit.TestContext();
        var restored = new NlQueryResult(
            NlResponseKind.QueryResult, "SELECT id FROM orders", null, null, null,
            [], [], RowCount: 214, Truncated: true, ElapsedMs: 38);

        var view = ctx.RenderComponent<ChatOutcome>(p => p
            .Add(c => c.Result, restored)
            .Add(c => c.Restored, true));

        Assert.Contains("214", view.Markup);
        Assert.Contains("38", view.Markup);
        Assert.Contains("truncated", view.Markup);
        Assert.Contains("Rows are not stored", view.Markup);
        Assert.Empty(view.FindAll("table"));
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/SqlAgent.Tests/SqlAgent.Tests.csproj --filter "ChatPageTests|ChatOutcomeTests"`
Expected: FAIL — compile error, `Chat` and the message components do not exist.

- [ ] **Step 3: Teach `ChatOutcome` about a restored answer**

In `src/SqlAgent.Host/Components/Shared/ChatOutcome.razor`, add the parameter and a case **above** the
existing `QueryResult` case — a `when` clause only wins if it is matched first:

```razor
        case NlResponseKind.QueryResult when Restored:
            @* Reloaded from the store, which keeps the numbers and not the rows. Rendering the usual
               table with an empty body would say "this query returned nothing", which is a different
               and wrong statement. *@
            <p class="meta">@r.RowCount rows · @r.ElapsedMs ms @(r.Truncated ? "· results truncated" : "")</p>
            <p class="restored-note">Rows are not stored. Open the query in the editor and run it again to see them.</p>
            break;
```

```csharp
    /// <summary>True when this outcome came back from the store rather than from a live call, so it has
    /// metadata but no rows.</summary>
    [Parameter] public bool Restored { get; set; }
```

Add to `src/SqlAgent.Host/Components/Shared/ChatOutcome.razor.css`:

```css
.restored-note { color: var(--text-100); font-size: var(--text-xs); }
```

- [ ] **Step 4: Let the page focus the composer**

Add to `src/SqlAgent.Host/Components/Shared/Chat/Composer.razor`'s `@code` block:

```csharp
    /// <summary>Moves the caret into the textarea. The suggestion chips fill the box and then call this,
    /// because a filled box the user still has to click into is a worse affordance than an empty one.</summary>
    public async Task FocusAsync()
    {
        try
        {
            await _textarea.FocusAsync();
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException or OperationCanceledException)
        {
            Logger.Log(ex is JSException ? LogLevel.Warning : LogLevel.Debug, ex,
                "Focusing the composer failed; the text is in the box but the caret is not.");
        }
    }
```

- [ ] **Step 5: Write the message components**

Create `src/SqlAgent.Host/Components/Shared/Chat/UserMessage.razor`:

```razor
<div class="message user-message">
    <div class="bubble">@Message.Text</div>
    @* Read-only: these attachments are what the question was actually sent with, not a choice still
       being made. *@
    <AttachmentChips Databases="Message.Databases" ReadOnly="true" />
</div>

@code {
    [Parameter, EditorRequired] public ChatMessageView Message { get; set; } = default!;
}
```

Create `src/SqlAgent.Host/Components/Shared/Chat/UserMessage.razor.css`:

```css
.user-message { display: flex; flex-direction: column; align-items: flex-end; gap: var(--space-2); }
.bubble {
  max-width: 70%;
  padding: var(--space-2) var(--space-4);
  border-radius: var(--radius-pill);
  background: var(--background-soft-100);
  color: var(--title-50);
  white-space: pre-wrap;
}
```

Create `src/SqlAgent.Host/Components/Shared/Chat/AssistantMessage.razor`:

```razor
<div class="message assistant-message">
    <ChatOutcome Result="Outcome" Restored="Live is null" OnOpenInEditor="OnOpenInEditor" />
</div>

@code {
    [Parameter, EditorRequired] public ChatMessageView Message { get; set; } = default!;

    /// <summary>The in-memory result for an answer produced in this circuit, rows included. Null for
    /// every message read back from the store, which is what makes ChatOutcome render the restored
    /// shape instead of an empty grid.</summary>
    [Parameter] public NlQueryResult? Live { get; set; }

    [Parameter] public EventCallback<string> OnOpenInEditor { get; set; }

    private NlQueryResult? Outcome => Live ?? Restore(Message);

    /// <summary>
    /// Rebuilds the outcome shape ChatOutcome renders from what the store keeps. Columns and rows are
    /// deliberately empty — they were never written — and the Restored flag above is what stops that
    /// emptiness being drawn as a result.
    /// </summary>
    private static NlQueryResult? Restore(ChatMessageView m) => m.OutcomeKind switch
    {
        ChatOutcomeKind.QueryResult => new NlQueryResult(
            NlResponseKind.QueryResult, m.GeneratedSql, null, null, null,
            [], [], m.RowCount ?? 0, m.Truncated, m.ElapsedMs ?? 0),
        ChatOutcomeKind.Clarification => NlQueryResult.Clarification(m.Text),
        ChatOutcomeKind.Error => NlQueryResult.Error(
            m.ErrorCode ?? "", m.Text, m.GeneratedSql, m.ElapsedMs ?? 0),
        _ => null,
    };
}
```

Create `src/SqlAgent.Host/Components/Shared/Chat/AssistantMessage.razor.css`:

```css
.assistant-message { display: flex; flex-direction: column; gap: var(--space-2); }
```

Create `src/SqlAgent.Host/Components/Shared/Chat/MessageList.razor`:

```razor
<div class="transcript">
    @foreach (var message in Messages)
    {
        @if (message.Role == ChatRole.User)
        {
            <UserMessage @key="message.Id" Message="message" />
        }
        else
        {
            @* Keyed by message id so appending a turn does not re-key the whole list and re-run every
               child's lifecycle — the transcript is the one place in this app that grows without bound. *@
            <AssistantMessage @key="message.Id" Message="message"
                              Live="@(Live.TryGetValue(message.Id, out var live) ? live : null)"
                              OnOpenInEditor="OnOpenInEditor" />
        }
    }
</div>

@code {
    [Parameter] public IReadOnlyList<ChatMessageView> Messages { get; set; } = [];

    /// <summary>Results produced in this circuit, by message id. Empty after a reload.</summary>
    [Parameter] public IReadOnlyDictionary<Guid, NlQueryResult> Live { get; set; } =
        new Dictionary<Guid, NlQueryResult>();

    [Parameter] public EventCallback<string> OnOpenInEditor { get; set; }
}
```

Create `src/SqlAgent.Host/Components/Shared/Chat/MessageList.razor.css`:

```css
.transcript { display: flex; flex-direction: column; gap: var(--space-5); }
```

- [ ] **Step 6: Write the page, and take the root route off `Workspace`**

Two components cannot both claim `/`: Blazor's router throws `AmbiguousMatchException` at navigation
time, not at build time, so the failure would land at runtime on the very first request. Delete the
temporary `@page "/"` line and its comment from `src/SqlAgent.Host/Components/Pages/Workspace.razor`
in the same commit that adds the page below. Task 7 left that line in place deliberately, so the suite
stayed green while the root had no other tenant; this is the moment it acquires one.

Create `src/SqlAgent.Host/Components/Pages/Chat.razor`:

```razor
@page "/"
@page "/chat/{Id:guid}"
@implements IDisposable
@inject ScopedRunner Runner
@inject AppState State
@inject NavigationManager Nav

<div class="chat">
    @if (_messages.Count == 0)
    {
        <div class="chat-hero">
            <h1>How can I help with your data?</h1>
            <p class="muted">Attach a database, then ask in plain language.</p>
        </div>
    }
    else
    {
        <MessageList Messages="_messages" Live="_live" OnOpenInEditor="OpenInEditor" />
    }

    <div class="chat-composer">
        <Composer @ref="_composer"
                  Value="_question" ValueChanged="v => _question = v"
                  Attached="_attached" OnRemoveAttachment="Detach"
                  Busy="_busy" OnSend="SendAsync" OnStop="Stop">
            <Tools>
                <AttachmentMenu Connections="_connections"
                                AttachedIds="@(_attached.Select(a => a.ConnectionId!.Value).ToList())"
                                OnAttach="Attach" />
            </Tools>
        </Composer>

        @if (_messages.Count == 0)
        {
            <div class="suggestions">
                @foreach (var suggestion in Suggestions)
                {
                    @* Prefill and focus, never send. One click on a chip with no model configured would
                       otherwise answer with an error panel before the question could be edited. *@
                    <button type="button" class="ghost suggestion" data-testid="suggestion"
                            @onclick="() => PrefillAsync(suggestion)">@suggestion</button>
                }
            </div>
        }
    </div>
</div>

@code {
    [Parameter] public Guid? Id { get; set; }

    private static readonly string[] Suggestions =
    [
        "Explain this schema",
        "Show table relationships",
        "Find the largest tables",
    ];

    private readonly List<ChatMessageView> _messages = [];
    private readonly Dictionary<Guid, NlQueryResult> _live = [];
    private readonly List<ChatDatabaseRef> _attached = [];
    private IReadOnlyList<DatabaseConnectionInfo> _connections = [];
    private Composer? _composer;
    private string _question = "";
    private bool _busy;
    private CancellationTokenSource? _cts;

    protected override async Task OnInitializedAsync()
    {
        // Connections change from the Connections page, which is a sibling route in the same circuit —
        // the same reason SchemaRail subscribes to this event rather than reading its list once.
        State.ConnectionsChanged += OnConnectionsChanged;
        await ReloadConnectionsAsync();
    }

    protected override async Task OnParametersSetAsync()
    {
        // The send below navigates to /chat/{id} after creating the chat, which re-enters this method
        // with the id of the conversation already in memory. Reloading it from the store there would
        // throw away _live and redraw the answer the user is looking at as a restored one, rows gone.
        if (Id == _loadedChatId) return;
        _loadedChatId = Id;
        _messages.Clear();
        _live.Clear();
        if (Id is { } id)
        {
            var detail = await Runner.RunAsync<ChatService, ChatDetail?>(s => s.GetChatAsync(id));
            if (detail is not null) _messages.AddRange(detail.Messages);
        }
        State.SetActiveChat(Id);
    }

    private Guid? _loadedChatId;

    private async Task ReloadConnectionsAsync()
    {
        _connections = await Runner.RunAsync<DatabaseConnectionService, IReadOnlyList<DatabaseConnectionInfo>>(
            s => s.ListAsync());
        // A connection deleted elsewhere must not stay in the chips: sending it would resolve to
        // "(deleted database)" and the user would have had no warning it was gone.
        _attached.RemoveAll(a => !_connections.Any(c => c.Id == a.ConnectionId));
    }

    private void OnConnectionsChanged() => InvokeAsync(async () =>
    {
        await ReloadConnectionsAsync();
        StateHasChanged();
    });

    private void Attach(DatabaseConnectionInfo connection)
    {
        if (_attached.Any(a => a.ConnectionId == connection.Id)) return;
        _attached.Add(new ChatDatabaseRef(connection.Id, connection.Name));
    }

    private void Detach(ChatDatabaseRef database) =>
        _attached.RemoveAll(a => a.ConnectionId == database.ConnectionId);

    private async Task PrefillAsync(string suggestion)
    {
        _question = suggestion;
        if (_composer is not null) await _composer.FocusAsync();
    }

    private async Task SendAsync()
    {
        // Mirrors the SQL page's guard, and for the same reason: Enter is a second entry point that does
        // not go through the button's disabled attribute.
        if (_busy || string.IsNullOrWhiteSpace(_question)) return;
        _busy = true;
        var question = _question;
        _question = "";
        var databaseIds = _attached.Select(a => a.ConnectionId!.Value).ToList();
        _cts = new CancellationTokenSource();
        try
        {
            var turn = await Runner.RunAsync<ChatTurnService, ChatTurnResult>(
                s => s.SendAsync(_loadedChatId, question, databaseIds, _cts.Token));

            _messages.Add(turn.UserMessage);
            _messages.Add(turn.AssistantMessage);
            if (turn.Live is { } live) _live[turn.AssistantMessage.Id] = live;

            // The chips deliberately stay: attachments are per message, but re-attaching every turn
            // would make a ten-question conversation ten attachments.
            if (_loadedChatId != turn.ChatId)
            {
                _loadedChatId = turn.ChatId;
                // replace, so Back does not walk into the same conversation's empty state.
                Nav.NavigateTo($"/chat/{turn.ChatId}", replace: true);
                State.SetActiveChat(turn.ChatId);
            }
            State.NotifyChatsChanged();
        }
        catch (OperationCanceledException)
        {
            // The user pressed stop. The question is already on disk (ChatTurnService writes it first),
            // so nothing is lost; the answer simply never arrived. Reloading the chat is what shows the
            // question again, which is honest about what happened.
        }
        finally
        {
            _busy = false;
            _cts.Dispose();
            _cts = null;
        }
    }

    private void Stop() => _cts?.Cancel();

    private void OpenInEditor(string sql)
    {
        // The editor is a separate route now, so the SQL travels through AppState rather than a tab flip.
        State.HandOffSql(sql);
        Nav.NavigateTo("/sql");
    }

    public void Dispose() => State.ConnectionsChanged -= OnConnectionsChanged;
}
```

Create `src/SqlAgent.Host/Components/Pages/Chat.razor.css`:

```css
.chat { display: flex; flex-direction: column; gap: var(--space-5); min-height: 100%; }

/* The hero pushes the composer to the middle of an empty page and collapses out of the way as soon as
   there is a transcript to read. */
.chat-hero { margin: auto auto var(--space-4); text-align: center; }
.chat-hero h1 { margin-bottom: var(--space-2); }

.chat-composer { position: sticky; bottom: 0; padding-top: var(--space-3); background: var(--background-100); }

.suggestions { display: flex; flex-wrap: wrap; gap: var(--space-2); margin-top: var(--space-3); }
.suggestion { border: 1px solid var(--base-200); border-radius: var(--radius-pill); font-size: var(--text-xs); }
```

- [ ] **Step 7: Run the tests to verify they pass**

Run: `dotnet test tests/SqlAgent.Tests/SqlAgent.Tests.csproj --filter "ChatPageTests|ChatOutcomeTests"`
Expected: PASS, 16 tests.

- [ ] **Step 8: Run the whole suite**

Run: `dotnet test SqlAgent.slnx --configuration Release`
Expected: PASS. `/` resolves again, so `TokenAuthTests` and the other integration tests that fetch the
root are meaningful once more.

- [ ] **Step 9: Commit**

```bash
git add src/SqlAgent.Host tests/SqlAgent.Tests
git commit -m "$(cat <<'COMMIT'
Put a real chat at the root route, with conversations that survive a reload

The chat row is written on first send, so opening a new chat and walking away
leaves nothing behind, and the first question becomes the title because there is
no model to summarize with. Chips carry over between questions while each sent
message keeps its own snapshot.

A reloaded answer shows its row count, duration and truncation flag with a line
saying the rows are not stored — the alternative, an empty table, would claim the
query returned nothing. That is the only place the rows-not-persisted rule is
visible, so it is the place that explains it.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
COMMIT
)"
```

---

### Task 10: The history section

**Files:**
- Create: `src/SqlAgent.Host/Components/Shared/Chat/ChatRenameDialog.razor`
- Create: `src/SqlAgent.Host/Components/Shared/Chat/ChatDeleteDialog.razor`
- Create: `src/SqlAgent.Host/Components/Layout/HistorySection.razor` + `.razor.css`
- Modify: `src/SqlAgent.Host/Components/Layout/Sidebar.razor`
- Test: `tests/SqlAgent.Tests/HistorySectionTests.cs`

**Interfaces:**
- Consumes: `ChatService`, `ChatHistoryGrouping`, `AppState.ChatsChanged`, `DialogService`, `Modal`, `Menu`.
- Produces:
  - `<ChatRenameDialog Chat="ChatSummary" OnSave="EventCallback<string>" OnCancel="EventCallback" />`
  - `<ChatDeleteDialog Chat="ChatSummary" OnConfirm="EventCallback" OnCancel="EventCallback" />`
  - `<HistorySection />`, rendered by `Sidebar` above the schema rail.

- [ ] **Step 1: Write the failing test**

Create `tests/SqlAgent.Tests/HistorySectionTests.cs`:

```csharp
using Bunit;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SqlAgent.Host.Components.Layout;
using SqlAgent.Host.Web;
using SqlAgent.Storage;

namespace SqlAgent.Tests;

public class HistorySectionTests : IDisposable
{
    private readonly SqliteConnection _conn = new("DataSource=:memory:");
    private readonly Bunit.TestContext _ctx = new();

    public HistorySectionTests()
    {
        _conn.Open();
        _ctx.Services.AddDbContext<SqlAgentDbContext>(o => o.UseSqlite(_conn));
        _ctx.Services.AddScoped<ChatService>();
        _ctx.Services.AddScoped<ScopedRunner>();
        _ctx.Services.AddScoped<AppState>();
        _ctx.Services.AddScoped<DialogService>();
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        using var scope = _ctx.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<SqlAgentDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Chats_are_listed_under_their_day_headings_newest_first()
    {
        await SeedAsync("this morning", DateTime.UtcNow.AddHours(-2));
        await SeedAsync("last month", DateTime.UtcNow.AddDays(-20));

        var section = _ctx.RenderComponent<HistorySection>();

        var headings = section.FindAll(".history-heading").Select(h => h.TextContent.Trim()).ToList();
        Assert.Equal("Today", headings[0]);
        Assert.Contains("Previous 30 days", headings);
        Assert.Contains("this morning", section.Markup);
    }

    [Fact]
    public async Task Nothing_but_an_explanation_shows_when_there_is_no_history()
    {
        var section = _ctx.RenderComponent<HistorySection>();

        Assert.Empty(section.FindAll(".history-row"));
        Assert.Contains("No chats yet", section.Markup);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task The_open_chat_is_marked_active()
    {
        // The sidebar is the only thing telling the user which conversation they are in once the
        // transcript scrolls past the first message.
        var id = await SeedAsync("open one", DateTime.UtcNow);
        _ctx.Services.GetRequiredService<AppState>().SetActiveChat(id);

        var section = _ctx.RenderComponent<HistorySection>();

        Assert.Contains("active", section.Find(".history-row").ClassName);
    }

    [Fact]
    public async Task The_list_re_reads_itself_when_the_page_says_history_changed()
    {
        // HistorySection and the chat page are siblings under MainLayout, so a chat created on the page
        // reaches this component only through AppState. Without the subscription the sidebar would show
        // whatever existed when the tab was opened, which is exactly the defect SchemaRail already had.
        var section = _ctx.RenderComponent<HistorySection>();
        Assert.Contains("No chats yet", section.Markup);

        await SeedAsync("brand new", DateTime.UtcNow);
        await section.InvokeAsync(_ctx.Services.GetRequiredService<AppState>().NotifyChatsChanged);

        Assert.Contains("brand new", section.Markup);
    }

    [Fact]
    public async Task Renaming_from_the_row_menu_goes_through_a_dialog_and_updates_the_store()
    {
        var id = await SeedAsync("first question, truncated", DateTime.UtcNow);
        var section = _ctx.RenderComponent<HistorySection>();
        var dialogs = _ctx.Services.GetRequiredService<DialogService>();

        section.Find(".history-row .menu-trigger").Click();
        section.FindAll(".menu-item-action").First(r => r.TextContent.Contains("Rename")).Click();

        // The dialog is handed to DialogService rather than rendered here: inside the drawer a Modal
        // would resolve its position against the sidebar's transform (Phase A carry-forward 1).
        Assert.NotNull(dialogs.Current);

        // Render what the host would render, and drive it.
        var dialog = _ctx.Render(dialogs.Current!);
        dialog.Find("input").Change("quarterly revenue");
        await dialog.Find("[data-testid=rename-save]").ClickAsync(new MouseEventArgs());

        Assert.Equal("quarterly revenue", (await LoadAsync(id)).Title);
        Assert.Null(dialogs.Current);
    }

    [Fact]
    public async Task Deleting_from_the_row_menu_asks_first_and_then_removes_the_chat()
    {
        var id = await SeedAsync("throwaway", DateTime.UtcNow);
        var section = _ctx.RenderComponent<HistorySection>();
        var dialogs = _ctx.Services.GetRequiredService<DialogService>();

        section.Find(".history-row .menu-trigger").Click();
        section.FindAll(".menu-item-action").First(r => r.TextContent.Contains("Delete")).Click();

        var dialog = _ctx.Render(dialogs.Current!);
        // The chat is named in the dialog: "are you sure?" with no subject is how the wrong one gets
        // deleted.
        Assert.Contains("throwaway", dialog.Markup);
        await dialog.Find("[data-testid=delete-confirm]").ClickAsync(new MouseEventArgs());

        Assert.Null(await LoadAsync(id));
        Assert.DoesNotContain("throwaway", section.Markup);
    }

    [Fact]
    public async Task Cancelling_the_delete_dialog_keeps_the_chat()
    {
        var id = await SeedAsync("keep me", DateTime.UtcNow);
        var section = _ctx.RenderComponent<HistorySection>();
        var dialogs = _ctx.Services.GetRequiredService<DialogService>();
        section.Find(".history-row .menu-trigger").Click();
        section.FindAll(".menu-item-action").First(r => r.TextContent.Contains("Delete")).Click();

        var dialog = _ctx.Render(dialogs.Current!);
        await dialog.Find("[data-testid=delete-cancel]").ClickAsync(new MouseEventArgs());

        Assert.NotNull(await LoadAsync(id));
        Assert.Null(dialogs.Current);
    }

    [Fact]
    public async Task Deleting_the_chat_that_is_open_clears_the_selection()
    {
        // Otherwise the sidebar keeps highlighting a row that no longer exists and the page keeps
        // showing a conversation that has been deleted from under it.
        var id = await SeedAsync("open one", DateTime.UtcNow);
        var state = _ctx.Services.GetRequiredService<AppState>();
        state.SetActiveChat(id);
        var section = _ctx.RenderComponent<HistorySection>();
        var dialogs = _ctx.Services.GetRequiredService<DialogService>();
        section.Find(".history-row .menu-trigger").Click();
        section.FindAll(".menu-item-action").First(r => r.TextContent.Contains("Delete")).Click();

        var dialog = _ctx.Render(dialogs.Current!);
        await dialog.Find("[data-testid=delete-confirm]").ClickAsync(new MouseEventArgs());

        Assert.Null(state.ActiveChatId);
    }

    private async Task<Guid> SeedAsync(string title, DateTime lastMessageAtUtc)
    {
        using var scope = _ctx.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SqlAgentDbContext>();
        var chat = new Chat
        {
            Id = Guid.NewGuid(),
            Title = title,
            CreatedAt = lastMessageAtUtc,
            UpdatedAt = lastMessageAtUtc,
            LastMessageAt = lastMessageAtUtc,
        };
        db.Chats.Add(chat);
        await db.SaveChangesAsync();
        return chat.Id;
    }

    private async Task<ChatDetail?> LoadAsync(Guid id)
    {
        using var scope = _ctx.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ChatService>().GetChatAsync(id);
    }

    public void Dispose()
    {
        _ctx.Dispose();
        _conn.Dispose();
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/SqlAgent.Tests/SqlAgent.Tests.csproj --filter HistorySectionTests`
Expected: FAIL — compile error, `HistorySection` does not exist.

- [ ] **Step 3: Write the two dialogs**

Create `src/SqlAgent.Host/Components/Shared/Chat/ChatRenameDialog.razor`:

```razor
@* Presentational: it collects a title and reports it. The rename itself happens in HistorySection,
   which owns the service scope — a dialog that writes to the store as a side effect of being rendered
   would be much harder to reason about from the caller. *@
<Modal Title="Rename chat" OnClose="OnCancel">
    <ChildContent>
        <label for="chat-title">Title</label>
        @* OnTitleChanged is a method rather than an inline lambda: a lambda with a string literal inside
           a Razor attribute needs escaped quotes, which the parser handles badly — SchemaRail's filter
           input carries the same note for the same reason. *@
        <input id="chat-title" value="@_title" @onchange="OnTitleChanged" />
    </ChildContent>
    <Footer>
        <button type="button" @onclick="OnCancel">Cancel</button>
        <button type="button" class="primary" data-testid="rename-save"
                disabled="@string.IsNullOrWhiteSpace(_title)"
                @onclick="() => OnSave.InvokeAsync(_title)">Save</button>
    </Footer>
</Modal>

@code {
    [Parameter, EditorRequired] public ChatSummary Chat { get; set; } = default!;
    [Parameter] public EventCallback<string> OnSave { get; set; }
    [Parameter] public EventCallback OnCancel { get; set; }

    private string _title = "";

    // OnInitialized, not a field initializer: parameters are not set yet when fields initialize.
    protected override void OnInitialized() => _title = Chat.Title;

    private void OnTitleChanged(ChangeEventArgs e) => _title = e.Value?.ToString() ?? string.Empty;
}
```

Create `src/SqlAgent.Host/Components/Shared/Chat/ChatDeleteDialog.razor`:

```razor
<Modal Title="Delete chat" OnClose="OnCancel">
    <ChildContent>
        @* The title is in the question. "Are you sure?" with no subject is how the wrong chat gets
           deleted from a list of twenty. *@
        <p>Delete <strong>@Chat.Title</strong> and its messages? This cannot be undone.</p>
    </ChildContent>
    <Footer>
        <button type="button" data-testid="delete-cancel" @onclick="OnCancel">Cancel</button>
        <button type="button" class="danger" data-testid="delete-confirm" @onclick="OnConfirm">Delete</button>
    </Footer>
</Modal>

@code {
    [Parameter, EditorRequired] public ChatSummary Chat { get; set; } = default!;
    [Parameter] public EventCallback OnConfirm { get; set; }
    [Parameter] public EventCallback OnCancel { get; set; }
}
```

- [ ] **Step 4: Write `HistorySection`**

Create `src/SqlAgent.Host/Components/Layout/HistorySection.razor`:

```razor
@implements IDisposable
@inject ScopedRunner Runner
@inject AppState State
@inject DialogService Dialogs
@inject NavigationManager Nav

<div class="history">
    @if (_chats.Count == 0)
    {
        <p class="history-empty muted">No chats yet</p>
    }
    else
    {
        @foreach (var group in ChatHistoryGrouping.Group(_chats, DateTime.Now))
        {
            <p class="history-heading">@group.Label</p>
            @foreach (var chat in group.Chats)
            {
                <div class="history-row @(chat.Id == State.ActiveChatId ? "active" : "")" @key="chat.Id">
                    @* Open(chat) rather than an inline lambda: the URL is a string literal inside a
                       Razor attribute, which needs escaping the parser handles badly. *@
                    <button type="button" class="ghost history-open truncate"
                            @onclick="() => Open(chat)">@chat.Title</button>
                    <Menu Placement="MenuPlacement.Bottom">
                        <Trigger>
                            <Icon Name="more-vertical" Size="16" />
                            <span class="sr-only">@($"Actions for {chat.Title}")</span>
                        </Trigger>
                        <ChildContent>
                            <MenuItem Icon="pencil" OnClick="() => ShowRename(chat)">Rename</MenuItem>
                            <MenuItem Icon="trash" Danger="true" OnClick="() => ShowDelete(chat)">Delete</MenuItem>
                        </ChildContent>
                    </Menu>
                </div>
            }
        }
    }
</div>

@code {
    private IReadOnlyList<ChatSummary> _chats = [];

    protected override async Task OnInitializedAsync()
    {
        // The chat page is a sibling route under MainLayout, not an ancestor, and MainLayout is not
        // recreated across navigation — so nothing re-runs this method when a chat is created, renamed
        // or deleted. The subscription is what keeps the list true; SchemaRail carries the same one for
        // the same reason.
        State.ChatsChanged += OnChatsChanged;
        await ReloadAsync();
    }

    private async Task ReloadAsync() =>
        _chats = await Runner.RunAsync<ChatService, IReadOnlyList<ChatSummary>>(s => s.ListHistoryAsync());

    private void Open(ChatSummary chat) => Nav.NavigateTo($"/chat/{chat.Id}");

    private void OnChatsChanged() => InvokeAsync(async () =>
    {
        await ReloadAsync();
        StateHasChanged();
    });

    // RenderFragment built in Razor rather than through RenderTreeBuilder by hand: the dialog is handed
    // to DialogService, which renders it from MainLayout. Rendering it here would put a position:fixed
    // element inside the sidebar, and below 1024px the sidebar's transform makes it the containing block
    // — the dialog would centre on the drawer and ride off-screen with it.
    private void ShowRename(ChatSummary chat) => Dialogs.Show(__builder =>
    {
        <ChatRenameDialog Chat="chat"
                          OnSave="title => RenameAsync(chat, title)"
                          OnCancel="Dialogs.Close" />
    });

    private void ShowDelete(ChatSummary chat) => Dialogs.Show(__builder =>
    {
        <ChatDeleteDialog Chat="chat"
                          OnConfirm="() => DeleteAsync(chat)"
                          OnCancel="Dialogs.Close" />
    });

    private async Task RenameAsync(ChatSummary chat, string title)
    {
        await Runner.RunAsync<ChatService, bool>(s => s.RenameChatAsync(chat.Id, title));
        Dialogs.Close();
        await ReloadAsync();
        StateHasChanged();
    }

    private async Task DeleteAsync(ChatSummary chat)
    {
        await Runner.RunAsync<ChatService, bool>(s => s.DeleteChatAsync(chat.Id));
        Dialogs.Close();
        // Clearing the selection first: leaving it set would keep highlighting a row that no longer
        // exists, and the page would keep showing a conversation deleted out from under it.
        if (State.ActiveChatId == chat.Id)
        {
            State.SetActiveChat(null);
            Nav.NavigateTo("/");
        }
        await ReloadAsync();
        StateHasChanged();
    }

    public void Dispose() => State.ChatsChanged -= OnChatsChanged;
}
```

Create `src/SqlAgent.Host/Components/Layout/HistorySection.razor.css`:

```css
.history { display: flex; flex-direction: column; gap: 2px; }

.history-heading {
  margin-top: var(--space-4);
  padding: 0 var(--space-2);
  color: var(--text-100);
  font-size: var(--text-xs);
  font-weight: 500;
}
.history-empty { padding: var(--space-3) var(--space-2); font-size: var(--text-xs); }

.history-row { display: flex; align-items: center; gap: var(--space-1); border-radius: var(--radius-control); }
.history-row:hover { background: var(--background-soft-100); }
.history-row.active { background: var(--primary-50); }

.history-open {
  flex: 1;
  min-width: 0;
  text-align: left;
  padding: var(--space-2);
  color: var(--text-50);
}
.history-row.active .history-open { color: var(--primary-500); font-weight: 500; }

/* The row menu appears on hover, and unconditionally while it is open or focused — a control that
   exists only under a mouse pointer is unreachable from the keyboard. */
.history-row ::deep .menu-root { opacity: 0; }
.history-row:hover ::deep .menu-root,
.history-row:focus-within ::deep .menu-root { opacity: 1; }
```

- [ ] **Step 5: Put it in the sidebar**

In `src/SqlAgent.Host/Components/Layout/Sidebar.razor`, inside `.sidebar-body`, above the rail:

```razor
    <div class="sidebar-body custom-scroll">
        <HistorySection />

        @* Phase C replaces the rail with the Databases section and the config page. Until then it is
           the only place a connection can be picked or a table hidden, so it stays. *@
        <SchemaRail />
    </div>
```

`ShellTests` renders `Sidebar` and will now resolve `ChatService` and `DialogService` through it. Add
both to `RegisterSidebarServices`:

```csharp
        ctx.Services.AddScoped<ChatService>();
        ctx.Services.AddScoped<DialogService>();
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test tests/SqlAgent.Tests/SqlAgent.Tests.csproj --filter "HistorySectionTests|ShellTests"`
Expected: PASS.

- [ ] **Step 7: Run the whole suite**

Run: `dotnet test SqlAgent.slnx --configuration Release`
Expected: PASS.

- [ ] **Step 8: Commit**

```bash
git add src/SqlAgent.Host tests/SqlAgent.Tests
git commit -m "$(cat <<'COMMIT'
List conversations in the sidebar, grouped by day, with rename and delete

The section subscribes to AppState rather than reading its list once: the chat
page is a sibling route and MainLayout is not recreated across navigation, so
nothing else would ever tell the sidebar a chat was created — the same defect the
schema rail's connection picker already had.

Rename and delete open their dialogs through DialogService so they render from
MainLayout. Rendering a Modal from inside the sidebar puts a position:fixed
element under the drawer's transform, which makes the drawer its containing
block: the dialog would centre on the drawer and ride off-screen with it. Delete
names the chat in the question, because "are you sure?" with no subject is how
the wrong one goes.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
COMMIT
)"
```

---

### Task 11: Documentation and phase verification

**Files:**
- Modify: `docs/web-ui.md`
- Modify: `README.md`

**Interfaces:**
- Consumes: everything above.
- Produces: no code.

- [ ] **Step 1: Document the chat in `docs/web-ui.md`**

Replace the "Chat needs an LLM provider — and none ships configured" section with the text below, and
leave the rest of that section's explanation of `llm_not_configured` intact underneath it:

```markdown
## Chats, and what is kept

`/` is a new chat; `/chat/{id}` is a stored one; the sidebar lists them grouped by day in local time.

- **The chat row is written on the first send.** Opening a new chat and navigating away leaves nothing
  behind. The title is the first question cut to 60 characters — there is no model to summarize with —
  and can be renamed from the `⋮` menu on its row.
- **Databases attach to a message**, from the composer's attachment menu, and are listed there by the
  name given in connection settings — the same name the MCP tools address them by. The chips carry over
  to the next question until removed, and every sent message keeps its own snapshot of what was
  attached, including the name. Deleting a connection therefore does not rewrite history: the
  transcript still says what the question was asked against.
- **Zero or several attached databases** answer with `no_database_attached` and
  `multiple_databases_unsupported`. The second is a limit of today's gateway, which takes one schema and
  returns one SQL string; querying the first attachment silently would misreport what was asked.
- **Result rows are never stored.** A reloaded answer shows its row count, duration and truncation flag
  with a note saying so; open the SQL in the editor and run it again to see the rows. This keeps the
  local store from becoming a shadow copy of production data.
- **Failed answers are stored too**, `llm_not_configured` among them. A reloaded conversation is the
  one the user watched, not a shorter edit of it.

The SQL editor has its own page at `/sql`. It is not going away: Phase D adds a scratchpad panel beside
the chat built from the same components.

## The store and its migrations

The SQLite store is versioned with EF Core migrations. A store created before this release has the
original tables and no `__EFMigrationsHistory`; startup stamps the initial migration as applied and then
migrates, so an existing store keeps its data. A migration that fails stops the host rather than running
against a half-migrated store — the log names the store path.
```

- [ ] **Step 2: Extend the manual regression checklist**

`docs/web-ui.md` already carries a checklist table. Add these rows:

```markdown
| Ask a question, reload the page | Question, answer, SQL and the database chips all come back; the result grid does not, and says why |
| Open a new chat, type nothing, navigate away | No new row appears in the sidebar |
| Send with no database attached, then with two | Both explain themselves; both survive a reload |
| Attach a database, send twice | The chip is still there for the second question |
| Delete a connection that an old message used | The old message still shows the name it was sent with |
| Rename and delete a chat from its `⋮` menu | Rename updates the row; delete asks first and names the chat |
| Do the same from inside the drawer below 1024px | The dialog centres on the viewport, not on the drawer, and survives the drawer closing |
| Tab through the page below 1024px with the drawer closed | Focus never enters the drawer |
| Open the drawer, close it with the scrim | Focus returns to the hamburger |
| Press Enter in the composer, then Shift+Enter | Enter sends; Shift+Enter adds a line and grows the box |
| Start the host against a store from before this release | It migrates, and the old connections are still listed |
```

- [ ] **Step 3: Update the README**

In `README.md`, the Web UI paragraph says "Details on the shell, the screens, the token, and the manual
regression checklist". Extend the sentence to name what is new:

```markdown
Details on the shell, the screens, chat persistence, the token, and the manual regression checklist for
the parts automated tests can't reach are in [`docs/web-ui.md`](docs/web-ui.md).
```

- [ ] **Step 4: Full verification**

```bash
dotnet build SqlAgent.slnx --configuration Release
dotnet test SqlAgent.slnx --configuration Release
```

Expected: build clean, all tests pass.

Then run the app and walk the new checklist rows by hand — the ones bUnit cannot reach are the ones that
broke most often in Phase A:

```bash
dotnet run --project src/SqlAgent.Host/SqlAgent.Host.csproj
```

Confirm in particular: the reload row (persistence is the phase), the drawer dialog row (carry-forward
item 1), the drawer tab-order row (item 3), and the Enter/Shift+Enter row (the only behaviour in this
phase that lives entirely in JS).

Verify carry-forward item 2 while you are in a browser: open the rename dialog and press Escape without
touching anything first. If it closes, `Modal`'s `autofocus` fires and
`UiInteractionTests.The_modal_close_button_autofocuses_…` is telling the truth. If it does not, record
that in the commit message and open the follow-up — the test asserting the mechanism is then wrong too.

- [ ] **Step 5: Migration rehearsal on a real store**

The integration test covers this, but the failure mode is severe enough to be worth seeing once:

```bash
git stash                      # or check out the previous commit in a scratch worktree
dotnet run --project src/SqlAgent.Host/SqlAgent.Host.csproj   # creates an old-shaped store
# add a connection through the UI, stop the host
git stash pop
dotnet run --project src/SqlAgent.Host/SqlAgent.Host.csproj   # same store, new build
```

Expected: the host starts, the log line about stamping the baseline appears once, and the connection
added under the old build is still listed.

- [ ] **Step 6: Commit**

```bash
git add docs/web-ui.md README.md
git commit -m "$(cat <<'COMMIT'
Document chat persistence, the attachment model, and the migration path

Records what survives a reload and what deliberately does not, why a message
rather than a chat owns its databases, why two attached databases are refused for
now, and what happens on first start against a store created before migrations
existed. Adds the eleven manual checks bUnit cannot reach — persistence across a
real reload, the dialog inside the mobile drawer, the drawer's tab order, and
Enter versus Shift+Enter, which lives entirely in JS.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
COMMIT
)"
```

---

## Phase B1 Definition of Done

- [ ] `dotnet build SqlAgent.slnx --configuration Release` is clean.
- [ ] `dotnet test SqlAgent.slnx --configuration Release` is green, every pre-existing test included.
- [ ] A store created by the previous release migrates and keeps its data — verified by test and once by hand.
- [ ] A chat survives a reload with its messages, their order, and their attachment snapshots.
- [ ] A new chat abandoned without sending leaves no row.
- [ ] Sending with zero or several databases attached produces the documented codes, visible again after a reload.
- [ ] History buckets correctly across day boundaries; rename and delete work from the `⋮` menu, delete behind a confirmation that names the chat.
- [ ] `/` is the chat, `/chat/{id}` opens a stored one, `/sql` runs SQL, `/connections` and `/settings` are unchanged.
- [ ] No component stylesheet contains a literal color.
- [ ] `docs/web-ui.md` documents chat persistence, the attachment model, the migration path, and the new manual checks.

## Self-Review Notes

Checked against the spec section by section:

| Spec requirement | Task |
|---|---|
| Initial migration matching today's model exactly | 1 |
| `MigrateAsync` replaces `EnsureCreatedAsync` | 1 |
| Baseline shim, covered by an integration test | 1 |
| A failed migration stops the host | 1 |
| `Chat`, `ChatMessage`, `ChatMessageDatabase`; enums as strings; cascade | 2 |
| Nullable connection id, non-null name snapshot | 2 |
| `Sequence` assignment and the two-tab retry | 2 |
| Result rows never persisted | 2 (schema), 9 (what the user sees) |
| Day bucketing in local time | 3 |
| `ChatTurnService`: persist first, branch, persist | 4 |
| `no_database_attached`, `multiple_databases_unsupported` | 4 |
| Failed answers persisted, `llm_not_configured` included | 4 |
| `AppState.ActiveChatId`, `ChatsChanged`, `PendingSql` | 5 |
| `DialogService` + host in `MainLayout` (carry-forward 1) | 5 |
| Collapsed-rule parity and scoping (carry-forward 7) | 6 |
| Closed drawer leaves the tab order; focus restored (carry-forward 3) | 6 |
| `Workspace` de-tabbed, moved to `/sql`, kept | 7 |
| Sidebar nav: New chat, SQL, Connections, Settings | 7 |
| Attachment menu with databases by name, empty state | 8 |
| Chips carry over; sent messages keep a snapshot | 8, 9 |
| `Spinner` and `EmptyState` get callers (carry-forward 9) | 8 |
| Enter sends, Shift+Enter newlines, send becomes stop | 8 |
| Chat page at `/` and `/chat/{id}`, lazy creation, 60-char title | 9 |
| Suggestions prefill and focus, never send | 9 |
| Restored answers show metadata, not an empty grid | 9 |
| History section, buckets, active row, `⋮` rename/delete | 10 |
| Delete behind a `Modal` with its `Footer` slot | 10 |
| Docs and manual checklist | 11 |
| `autofocus` verified in a browser (carry-forward 2) | 11 |

**One deliberate divergence from the spec.** The spec's carry-forward table says item 7 should "delete
the duplicate set and keep one source of truth, the way the theme already works". Reading Phase A's
tests showed why that is wrong: four `ShellTests` facts pin *both* rule sets, and they pin them for a
reason the spec did not have in view — the pre-paint rule in `app.css` is the only collapsed styling in
effect until the circuit connects, and forever if it never does, while the scoped rule is what a
`localStorage` read-back applies afterwards. Making the circuit toggle the `<html>` class instead would
also make the visible collapse depend on interop succeeding, which Phase A deliberately decoupled (see
`A_browser_that_cannot_persist_the_collapse_choice_does_not_take_the_circuit_down`). Task 6 therefore
takes the other option the same table offers: a parity test plus scoping the selectors under the
sidebar. **Update that row in the spec when this task lands.**

**Types used across tasks, defined once:** `ChatRole`, `ChatOutcomeKind`, `ChatDatabaseRef`,
`ChatSummary`, `ChatMessageView`, `ChatDetail`, `ChatMessageInput`, `ChatTurnResult`, `HistoryBucket`,
`HistoryGroup`, `StoreInitializer.InitializeAsync`, `ChatService.*`, `ChatTurnService.SendAsync`,
`AppState.{ActiveChatId,SetActiveChat,ChatsChanged,NotifyChatsChanged,HandOffSql,TakePendingSql}`,
`DialogService.{Current,Changed,Show,Close}`, `Composer.{SendFromEditor,FocusAsync}`,
`window.sqlAgentComposer.{bind,unbind}`.

**Deferred to B2, and why:** projects (`Project`, `Chat.ProjectId`, the sidebar section, move-to-project
in the `⋮` menu) and search with `Ctrl`/`Cmd`+K, because both are whole features rather than parts of
persistence; Phase A carry-forward 8 (Safari does not focus a button on mouse click), which waits for
B2's document-level key listener to make a fix worth its cost; and carry-forward 11 and 12, two tests
that do not test what their names claim.
