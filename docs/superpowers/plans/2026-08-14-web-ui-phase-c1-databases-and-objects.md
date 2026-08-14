# Web UI Phase C1 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give the app a Databases sidebar section and a `/database/{id?}` config page that replace `/connections` and the schema rail, extract views from both engines, and enforce a three-way per-object access level on the execution path.

**Architecture:** Core gains views on `DatabaseSchema`, write-target roles on `ParsedStatement`, and a single policy resolver replacing the visibility predicate. Storage widens `TablePolicyService` to two dimensions, teaches `QueryExecutionService` to read the cached schema for view detection, and moves connection-test failures behind a classified enum so no driver text reaches the browser. The Host replaces two surfaces with one page and one sidebar section.

**Tech Stack:** .NET 10, Blazor Server, EF Core 10 + SQLite, SqlParserCS, Npgsql, Microsoft.Data.SqlClient, xUnit + bUnit.

**Spec:** `docs/superpowers/specs/2026-08-14-web-ui-phase-c1-databases-and-objects-design.md`

## Global Constraints

- **No policy row means Full access.** An object with no `TablePolicy` row is visible, readable, and writable. Every layer must agree; the connection's own `IsReadOnly` flag remains the coarse gate.
- **Views are identified from the cached schema, never from a policy row.** `TablePolicy` gains no `ObjectKind` column in this phase.
- **Driver exception text never reaches the browser.** It goes to the server log. Refusals render through `OutcomeMessage` with a stable code; failures render a fixed sentence with no code.
- **Nothing lives in the codebase that nothing renders.** Do not add an icon, column, or parameter this phase does not use.
- **Every commit builds and the full suite passes.** Signature changes update their callers in the same task.
- **Out of scope, do not build:** panel 3, `SqlStatementKind.Ddl`, `DdlOperation`, `AllowedDdl`, `policy_denied_ddl`, the `confirmed` flag, `ConfirmationRequired`. These are phase C2.
- **Test command:** `dotnet test tests/SqlAgent.Tests/SqlAgent.Tests.csproj`. A single test: append `--filter "FullyQualifiedName~TestName"`.
- **Build command:** `dotnet build`.

## File Structure

**Core (`src/SqlAgent.Core/`)**

| File | Responsibility after this phase |
|---|---|
| `SchemaModel.cs` | `DatabaseSchema` carries `Views`; `SchemaView` added; `Build` accepts view rows; `Filter` filters views. |
| `Policy/SqlPolicy.cs` | `ParsedStatement` carries `WrittenTables`; `ObjectAccess`/`ObjectPolicy` added; `Validate` takes one resolver and gains two denial branches. |
| `DatabaseProvider.cs` | `ConnectionFailure` added; `ConnectionTestResult` reshaped around it. |

**Providers**

| File | Responsibility after this phase |
|---|---|
| `SqlAgent.Providers.SqlServer/SqlServerProvider.cs` | Splits `TABLE_TYPE` into tables and views; classifies `SqlException.Number`. |
| `SqlAgent.Providers.SqlServer/SqlServerFailure.cs` | New. Pure classifier — error number and flags in, `ConnectionFailure` out. |
| `SqlAgent.Providers.Postgres/PostgresProvider.cs` | Splits `relkind` into tables and views; classifies `PostgresException.SqlState`. |
| `SqlAgent.Providers.Postgres/PostgresFailure.cs` | New. Pure classifier — SQL state and flags in, `ConnectionFailure` out. |

**Storage (`src/SqlAgent.Storage/`)**

| File | Responsibility after this phase |
|---|---|
| `TablePolicyService.cs` | Lists tables and views with levels; sets one object or a whole schema; invalidates the cache on every write. |
| `QueryExecutionService.cs` | Builds the policy resolver from policy rows plus the cached schema's view list. |
| `ConnectionTester.cs` | The boundary: logs `Diagnostic`, returns `ConnectionTestOutcome`. |
| `StoreInitializer.cs` | Copies the store to `.bak` before applying pending migrations. |
| `Migrations/*_ClearSchemaCacheForViews.cs` | New. Data-only migration clearing `SchemaCache`. |

**Host (`src/SqlAgent.Host/`)**

| File | Responsibility after this phase |
|---|---|
| `Web/AppState.cs` | Per-connection test status for the circuit, with its own change event. |
| `Components/Layout/DatabaseSection.razor` | New. The sidebar section. |
| `Components/Pages/DatabasePage.razor` | New. `/database/{id?}`, hosting both panels. |
| `Components/Shared/Database/ConnectionPanel.razor` | New. Panel 1. |
| `Components/Shared/Database/ObjectsPanel.razor` | New. Panel 2. |
| `Components/Shared/Database/ConnectionDeleteDialog.razor` | New. Confirmation before deleting a connection. |
| `Components/Shared/Ui/Modal.razor` | Refocuses when a caller's `FocusSignal` changes. |
| `Components/Shared/Chat/NameDialog.razor` | Raises the signal when an error arrives. |
| `Components/Pages/Workspace.razor` | Owns the connection picker. |
| `Components/Layout/Sidebar.razor` | Hosts `DatabaseSection`; its keydown comment no longer cites `SchemaRail`. |
| `Components/Layout/SidebarNav.razor` | "Databases" row pointing at `/database`. |
| Deleted | `Components/Layout/SchemaRail.razor`, `Components/Pages/Connections.razor` |

**Tests (`tests/SqlAgent.Tests/`)**

New: `RepoHygieneTests.cs`, `SchemaViewTests.cs`, `WriteTargetTests.cs`, `ConnectionFailureTests.cs`, `TablePolicyServiceTests.cs`, `DatabaseSectionTests.cs`, `DatabasePageTests.cs`, `ObjectsPanelTests.cs`.
Extended: `StoreMigrationTests.cs`, `SqlPolicyValidatorTests.cs`, `QueryExecutionServiceTests.cs`, `SchemaModelTests.cs`, `ProviderIntegrationTests.cs`, `WorkspaceTests.cs`, `NameDialogTests.cs`, `UiPrimitiveTests.cs`.
Deleted: `SchemaRailTests.cs`, `ConnectionsPageTests.cs`.

---

## Task 1: Ignore the launch token and the local store

The launch token is one careless `git add -A` from being committed. A review run in phase B2 left both files in `src/SqlAgent.Host/` and they had to be deleted by hand.

**Files:**
- Modify: `.gitignore`
- Test: `tests/SqlAgent.Tests/RepoHygieneTests.cs` (create)

**Interfaces:**
- Consumes: `RepoPaths.Find` from `tests/SqlAgent.Tests/DesignSystemTests.cs`.
- Produces: nothing other tasks use.

- [ ] **Step 1: Write the failing test**

Create `tests/SqlAgent.Tests/RepoHygieneTests.cs`:

```csharp
namespace SqlAgent.Tests;

/// <summary>
/// The host writes two files into its own source directory at run time: launch-url.txt, which carries a
/// live launch token, and sqlagent.db, the local store. Neither belongs in the repository, and a phase B2
/// review run left both behind. A test rather than a habit, because the failure mode is silent until the
/// token is already in someone's history.
/// </summary>
public class RepoHygieneTests
{
    [Theory]
    [InlineData("launch-url.txt")]
    [InlineData("sqlagent.db")]
    [InlineData("sqlagent.db-wal")]
    [InlineData("sqlagent.db-shm")]
    public void Run_time_artifacts_are_gitignored(string entry)
    {
        var lines = File.ReadAllLines(RepoPaths.Find(".gitignore"))
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains(entry, lines);
    }
}
```

- [ ] **Step 2: Run it and watch it fail**

Run: `dotnet test tests/SqlAgent.Tests/SqlAgent.Tests.csproj --filter "FullyQualifiedName~RepoHygieneTests"`
Expected: FAIL — four cases, each `Assert.Contains() Failure`.

- [ ] **Step 3: Add the entries**

Append to `.gitignore`:

```gitignore
# Written by the host into its own source directory at run time. launch-url.txt holds a live launch
# token; sqlagent.db is the local store, with its SQLite journal files.
launch-url.txt
sqlagent.db
sqlagent.db-wal
sqlagent.db-shm
```

- [ ] **Step 4: Run it and watch it pass**

Run: `dotnet test tests/SqlAgent.Tests/SqlAgent.Tests.csproj --filter "FullyQualifiedName~RepoHygieneTests"`
Expected: PASS, 4 tests.

- [ ] **Step 5: Confirm nothing is already tracked**

Run: `git ls-files | grep -E "launch-url\.txt|sqlagent\.db"`
Expected: no output. If anything prints, run `git rm --cached <path>` for each and mention it in the commit body — a file already tracked stays tracked no matter what `.gitignore` says.

- [ ] **Step 6: Commit**

```bash
git add .gitignore tests/SqlAgent.Tests/RepoHygieneTests.cs
git commit -m "Ignore the launch token and the local store"
```

---

## Task 2: Copy the store before applying pending migrations

Recommended by B1's final review and again by B2's, and deferred both times. A migration that succeeds but is wrong is unrecoverable today; with a `.bak` it is a file rename.

**Files:**
- Modify: `src/SqlAgent.Storage/StoreInitializer.cs`
- Test: `tests/SqlAgent.Tests/StoreMigrationTests.cs`

**Interfaces:**
- Consumes: `StoreInitializer.InitializeAsync(SqlAgentDbContext, ILogger, CancellationToken)` — signature unchanged.
- Produces: a `<store>.bak` file beside the store whenever `GetPendingMigrationsAsync()` returned anything.

- [ ] **Step 1: Write the failing tests**

Add to `tests/SqlAgent.Tests/StoreMigrationTests.cs`, inside the `StoreMigrationTests` class:

```csharp
    [Fact]
    public async Task A_store_with_pending_migrations_is_copied_before_they_are_applied()
    {
        // The backup exists for the migration that succeeds and is wrong — the case no test can catch,
        // because the store is left readable and plausible. Recovering from it is a file rename only if
        // the copy was taken before the migrator touched anything.
        await using (var legacy = NewLegacyContext())
        {
            await legacy.Database.EnsureCreatedAsync();
            legacy.Set<DatabaseConnection>().Add(new DatabaseConnection
            {
                Id = Guid.NewGuid(),
                Name = "prod",
                ProviderType = DatabaseProviderType.Postgres,
                ConnectionStringSecretRef = "db:abc",
                IsReadOnly = true,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            await legacy.SaveChangesAsync();
        }
        SqliteConnection.ClearAllPools();

        await using var db = NewContext();
        await StoreInitializer.InitializeAsync(db, NullLogger.Instance);

        var backup = DbPath + ".bak";
        Assert.True(File.Exists(backup), $"Expected a backup at {backup}.");

        // The copy must be the store as it was BEFORE the migration, not after: opened through the
        // pre-B1 model it still reads, and it must not have the tables the migration went on to add.
        SqliteConnection.ClearAllPools();
        await using var restored = new SqlAgentDbContext(
            new DbContextOptionsBuilder<SqlAgentDbContext>()
                .UseSqlite($"Data Source={backup}").Options);
        var tables = await restored.Database
            .SqlQueryRaw<string>("SELECT name AS Value FROM sqlite_master WHERE type = 'table'")
            .ToListAsync();
        Assert.Contains("DatabaseConnections", tables);
        Assert.DoesNotContain("Chats", tables);
    }

    [Fact]
    public async Task A_store_with_nothing_pending_is_not_copied()
    {
        // Every host start runs InitializeAsync. Writing a copy of the whole store on each one would
        // grow a file nobody asked for and overwrite the one backup that mattered.
        await using (var first = NewContext())
            await StoreInitializer.InitializeAsync(first, NullLogger.Instance);
        SqliteConnection.ClearAllPools();

        var backup = DbPath + ".bak";
        if (File.Exists(backup)) File.Delete(backup);

        await using var db = NewContext();
        await StoreInitializer.InitializeAsync(db, NullLogger.Instance);

        Assert.False(File.Exists(backup), "A store with no pending migrations must not be copied.");
    }

    [Fact]
    public async Task A_failed_backup_does_not_stop_the_migration()
    {
        // The backup is insurance, not a precondition. A read-only directory or a locked .bak must not
        // be the reason a host cannot start — that would turn a safety net into a new outage.
        await using (var legacy = NewLegacyContext())
            await legacy.Database.EnsureCreatedAsync();
        SqliteConnection.ClearAllPools();

        // A directory where the .bak file needs to go: File.Copy cannot overwrite it, so the copy throws
        // and the migration must proceed regardless.
        Directory.CreateDirectory(DbPath + ".bak");

        var provider = new RecordingLoggerProvider();
        await using var db = NewContext();
        await StoreInitializer.InitializeAsync(db, provider.CreateLogger("StoreInitializer"));

        Assert.NotEmpty(await db.Database.GetAppliedMigrationsAsync());
        Assert.Contains(provider.Records, r => r.Level == LogLevel.Warning);
    }
```

- [ ] **Step 2: Run them and watch them fail**

Run: `dotnet test tests/SqlAgent.Tests/SqlAgent.Tests.csproj --filter "FullyQualifiedName~StoreMigrationTests"`
Expected: the three new tests FAIL (`Expected a backup at ...`); the existing seven still PASS.

- [ ] **Step 3: Write the backup**

In `src/SqlAgent.Storage/StoreInitializer.cs`, replace the single line `await db.Database.MigrateAsync(ct);` inside the `try` with:

```csharp
            var pending = await db.Database.GetPendingMigrationsAsync(ct);
            if (pending.Any())
            {
                logger.LogInformation(
                    "Applying {Count} pending migration(s): {Migrations}.", pending.Count(), string.Join(", ", pending));
                BackUp(db, logger);
            }

            await db.Database.MigrateAsync(ct);
```

and add, below `StampAsync`:

```csharp
    /// <summary>
    /// Copies the store to "&lt;store&gt;.bak" before a migration runs. The case this exists for is the
    /// migration that succeeds and is wrong: the store is left readable and plausible, no test can catch
    /// it, and without a copy the only recovery is retyping every connection. With one, it is a rename.
    ///
    /// Best-effort by design. A read-only directory or a locked .bak must not be the reason the host
    /// cannot start — that turns a safety net into a new outage — so every failure here is logged and
    /// swallowed. It is deliberately NOT inside the caller's try/catch, which rethrows.
    ///
    /// The connection is closed first. SQLite may hold the tail of a write in -wal, and copying the main
    /// file alone while that is outstanding yields a backup missing the most recent commits. Closing
    /// checkpoints and removes the journal, so the single file copied is the whole store.
    /// </summary>
    private static void BackUp(SqlAgentDbContext db, ILogger logger)
    {
        try
        {
            var connection = (SqliteConnection)db.Database.GetDbConnection();
            var path = connection.DataSource;
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;

            db.Database.CloseConnection();
            SqliteConnection.ClearPool(connection);

            File.Copy(path, path + ".bak", overwrite: true);
            logger.LogInformation("Copied the store to {Backup} before migrating.", path + ".bak");
        }
        catch (Exception ex)
        {
            // Warning, not Error: nothing is broken and the migration is about to proceed, but a start
            // that migrated without a usable backup is exactly what someone will want to know about
            // afterwards. Only the message, never the path's directory contents.
            logger.LogWarning(ex, "The store could not be copied before migrating; continuing without a backup.");
        }
    }
```

- [ ] **Step 4: Run the tests and watch them pass**

Run: `dotnet test tests/SqlAgent.Tests/SqlAgent.Tests.csproj --filter "FullyQualifiedName~StoreMigrationTests"`
Expected: PASS, 10 tests.

- [ ] **Step 5: Run the full suite**

Run: `dotnet test tests/SqlAgent.Tests/SqlAgent.Tests.csproj`
Expected: PASS. `A_migration_failure_is_logged_with_the_file_path_not_the_full_connection_string` now also takes a backup on its way to failing — check its assertion still holds, since it asserts on the single Error record and the backup logs at Information.

- [ ] **Step 6: Commit**

```bash
git add src/SqlAgent.Storage/StoreInitializer.cs tests/SqlAgent.Tests/StoreMigrationTests.cs
git commit -m "Copy the store before applying pending migrations"
```

---

## Task 3: Views in the common schema model

**Files:**
- Modify: `src/SqlAgent.Core/SchemaModel.cs`
- Test: `tests/SqlAgent.Tests/SchemaViewTests.cs` (create)

**Interfaces:**
- Produces, used by Tasks 4, 5, 9, 10:
  - `record SchemaView(string Schema, string Name, IReadOnlyList<SchemaColumn> Columns)`
  - `DatabaseSchema(IReadOnlyList<SchemaTable> Tables, IReadOnlyList<SchemaView>? Views = null)` with `IReadOnlyList<SchemaView> ViewList => Views ?? []`
  - `SchemaModel.Build(columns, primaryKeys, foreignKeys, indexes = null, views = null)` where `views` rows are `(string Schema, string View, string Column, string DataType, bool Nullable, int? MaxLength, int? Precision, int? Scale)`
  - `SchemaModel.Filter(DatabaseSchema, Func<string, string, bool> isVisible)` — now filters views by the same predicate.

- [ ] **Step 1: Write the failing tests**

Create `tests/SqlAgent.Tests/SchemaViewTests.cs`:

```csharp
using SqlAgent.Core;

namespace SqlAgent.Tests;

/// <summary>
/// Views enter the model the same way tables do — flat catalog rows in, grouped objects out — and leave
/// it the same way: through the one visibility predicate. A view that survives Filter when its table
/// twin would not is a hidden object reaching the prompt.
/// </summary>
public class SchemaViewTests
{
    private static (string, string, string, string, bool, int?, int?, int?) Col(
        string schema, string obj, string column, string type = "int", bool nullable = false)
        => (schema, obj, column, type, nullable, null, null, null);

    [Fact]
    public void A_schema_built_without_view_rows_reports_no_views()
    {
        // Every existing call site passes nothing. ViewList, not Views, is what callers read, so the
        // absent case must be an empty list rather than a null they have to guard.
        var schema = SchemaModel.Build([Col("dbo", "Orders", "Id")], [], []);

        Assert.Empty(schema.ViewList);
        Assert.Null(schema.Views);
    }

    [Fact]
    public void View_rows_group_into_views_with_their_columns_in_order()
    {
        var schema = SchemaModel.Build(
            [Col("dbo", "Orders", "Id")],
            [], [], null,
            [Col("dbo", "OrderSummary", "OrderId"), Col("dbo", "OrderSummary", "Total", "decimal"),
             Col("sales", "TopCustomers", "Name", "varchar")]);

        Assert.Single(schema.Tables);
        Assert.Equal(2, schema.ViewList.Count);

        var summary = schema.ViewList.Single(v => v.Name == "OrderSummary");
        Assert.Equal("dbo", summary.Schema);
        Assert.Equal(["OrderId", "Total"], summary.Columns.Select(c => c.Name));
        Assert.Equal("decimal", summary.Columns[1].DataType);

        Assert.Equal("sales", schema.ViewList.Single(v => v.Name == "TopCustomers").Schema);
    }

    [Fact]
    public void A_table_and_a_view_of_the_same_name_in_different_schemas_stay_separate()
    {
        // The unique index on (connection, schema, name) makes this legal, and the grouping key must be
        // the pair rather than the bare name or the two collapse into one object.
        var schema = SchemaModel.Build(
            [Col("dbo", "Orders", "Id")],
            [], [], null,
            [Col("sales", "Orders", "Id")]);

        Assert.Equal("dbo", Assert.Single(schema.Tables).Schema);
        Assert.Equal("sales", Assert.Single(schema.ViewList).Schema);
    }

    [Fact]
    public void Filter_drops_a_hidden_view_and_keeps_a_visible_one()
    {
        var schema = SchemaModel.Build(
            [Col("dbo", "Orders", "Id")],
            [], [], null,
            [Col("dbo", "Secret", "Id"), Col("dbo", "Public", "Id")]);

        var filtered = SchemaModel.Filter(schema, (s, n) => n != "Secret");

        Assert.Equal("Public", Assert.Single(filtered.ViewList).Name);
        Assert.Single(filtered.Tables);
    }

    [Fact]
    public void Filter_applies_the_same_predicate_to_a_view_as_to_a_table()
    {
        // The predicate takes (schema, name) and cannot tell the two kinds apart. That is the point:
        // one rule, so a view cannot be the thing that quietly stays visible.
        var schema = SchemaModel.Build(
            [Col("dbo", "Orders", "Id")],
            [], [], null,
            [Col("dbo", "Orders2", "Id")]);

        var seen = new List<string>();
        SchemaModel.Filter(schema, (s, n) => { seen.Add($"{s}.{n}"); return true; });

        Assert.Contains("dbo.Orders", seen);
        Assert.Contains("dbo.Orders2", seen);
    }

    [Fact]
    public void Filter_returns_an_empty_view_list_rather_than_null_when_there_were_none()
    {
        var filtered = SchemaModel.Filter(SchemaModel.Build([Col("dbo", "Orders", "Id")], [], []), (_, _) => true);

        Assert.Empty(filtered.ViewList);
    }
}
```

- [ ] **Step 2: Run them and watch them fail**

Run: `dotnet test tests/SqlAgent.Tests/SqlAgent.Tests.csproj --filter "FullyQualifiedName~SchemaViewTests"`
Expected: FAIL to compile — `'DatabaseSchema' does not contain a definition for 'ViewList'`.

- [ ] **Step 3: Add the view record and widen DatabaseSchema**

In `src/SqlAgent.Core/SchemaModel.cs`, replace the `DatabaseSchema` declaration:

```csharp
/// <summary>
/// Provider-neutral description of a database's structure (CD-50 T4). <see cref="Views"/> is a defaulted
/// trailing parameter so no existing call site breaks and JSON written by a build that predated views
/// deserializes with none — read it through <see cref="ViewList"/> rather than guarding for null at every
/// use.
/// </summary>
public record DatabaseSchema(
    IReadOnlyList<SchemaTable> Tables,
    IReadOnlyList<SchemaView>? Views = null)
{
    [JsonIgnore]
    public IReadOnlyList<SchemaView> ViewList => Views ?? [];
}

/// <summary>
/// One view, with its columns. No primary key, foreign keys, or indexes: a view has none, and the column
/// list is what query generation actually needs. The view's defining SQL is deliberately absent — it is
/// unbounded text headed for a prompt, and including it needs a budget story this phase does not have.
/// </summary>
public record SchemaView(string Schema, string Name, IReadOnlyList<SchemaColumn> Columns);
```

- [ ] **Step 4: Accept view rows in Build**

Add a trailing parameter to `SchemaModel.Build` and build the list. The full method after the change:

```csharp
    public static DatabaseSchema Build(
        IEnumerable<(string Schema, string Table, string Column, string DataType, bool Nullable,
            int? MaxLength, int? Precision, int? Scale)> columns,
        IEnumerable<(string Schema, string Table, string Column)> primaryKeys,
        IEnumerable<(string Schema, string Table, string Column, string RefSchema, string RefTable, string RefColumn)> foreignKeys,
        // Index rows are (schema, table, index, column, ordinal, unique). Providers that don't expose index
        // metadata pass nothing — the table simply carries no indexes. Rows must be ORDER BY index, ordinal.
        IEnumerable<(string Schema, string Table, string Index, string Column, bool Unique)>? indexes = null,
        // View column rows, same shape as the table column rows above and ordered the same way. Trailing
        // and defaulted so a provider that does not extract views passes nothing.
        IEnumerable<(string Schema, string View, string Column, string DataType, bool Nullable,
            int? MaxLength, int? Precision, int? Scale)>? views = null)
    {
        var pkByTable = primaryKeys
            .GroupBy(p => (p.Schema, p.Table))
            .ToDictionary(g => g.Key, g => g.Select(p => p.Column).ToList());

        var fkByTable = foreignKeys
            .GroupBy(f => (f.Schema, f.Table))
            .ToDictionary(
                g => g.Key,
                g => g.Select(f => new ForeignKey(f.Column, f.RefSchema, f.RefTable, f.RefColumn)).ToList());

        var ixByTable = (indexes ?? [])
            .GroupBy(i => (i.Schema, i.Table))
            .ToDictionary(
                g => g.Key,
                g => g.GroupBy(i => i.Index)
                      .Select(ix => new SchemaIndex(ix.Key, ix.Select(c => c.Column).ToList(), ix.First().Unique))
                      .ToList());

        var tables = columns
            .GroupBy(c => (c.Schema, c.Table))
            .Select(g => new SchemaTable(
                g.Key.Schema,
                g.Key.Table,
                g.Select(c => new SchemaColumn(c.Column, c.DataType, c.Nullable, c.MaxLength, c.Precision, c.Scale)).ToList(),
                pkByTable.GetValueOrDefault(g.Key, []),
                fkByTable.GetValueOrDefault(g.Key, []),
                ixByTable.GetValueOrDefault(g.Key, [])))
            .ToList();

        // Null rather than an empty list when no rows were supplied: Build's own contract is that a
        // provider which does not extract views produces a schema indistinguishable from one written
        // before views existed, so a cached-JSON round trip cannot tell the two apart.
        var viewList = views is null
            ? null
            : views
                .GroupBy(v => (v.Schema, v.View))
                .Select(g => new SchemaView(
                    g.Key.Schema,
                    g.Key.View,
                    g.Select(c => new SchemaColumn(c.Column, c.DataType, c.Nullable, c.MaxLength, c.Precision, c.Scale)).ToList()))
                .ToList();

        return new DatabaseSchema(tables, viewList);
    }
```

- [ ] **Step 5: Filter views by the same predicate**

Replace the body of `SchemaModel.Filter`:

```csharp
    /// <summary>
    /// Drops objects the policy says are invisible (CD-50 visibility). Foreign keys that point at a
    /// now-hidden table are also dropped, so a hidden table's name never leaks through a relationship.
    /// Views go through the same predicate as tables — the predicate takes (schema, name) and cannot tell
    /// the two apart, which is the point: one rule, so a view is never the object that quietly stays
    /// visible after its table twin was hidden.
    /// </summary>
    public static DatabaseSchema Filter(DatabaseSchema schema, Func<string, string, bool> isVisible)
    {
        var visible = schema.Tables.Where(t => isVisible(t.Schema, t.Name)).ToList();
        var kept = visible.Select(t => (t.Schema, t.Name)).ToHashSet();

        var filtered = visible
            .Select(t => t with
            {
                ForeignKeys = t.ForeignKeys
                    .Where(fk => kept.Contains((fk.ReferencedSchema, fk.ReferencedTable)))
                    .ToList(),
            })
            .ToList();

        var views = schema.ViewList.Where(v => isVisible(v.Schema, v.Name)).ToList();

        return new DatabaseSchema(filtered, views);
    }
```

- [ ] **Step 6: Run the tests and watch them pass**

Run: `dotnet test tests/SqlAgent.Tests/SqlAgent.Tests.csproj --filter "FullyQualifiedName~SchemaViewTests"`
Expected: PASS, 6 tests.

- [ ] **Step 7: Run the full suite**

Run: `dotnet test tests/SqlAgent.Tests/SqlAgent.Tests.csproj`
Expected: PASS. `SchemaModelTests` and `SchemaServiceTests` are unaffected — every existing `Build` call omits the new parameter and every existing `Filter` call now gets an empty view list where it previously got null.

- [ ] **Step 8: Commit**

```bash
git add src/SqlAgent.Core/SchemaModel.cs tests/SqlAgent.Tests/SchemaViewTests.cs
git commit -m "Carry views through the common schema model"
```

---

## Task 4: Extract views from SQL Server

**Files:**
- Modify: `src/SqlAgent.Providers.SqlServer/SqlServerProvider.cs:32-44,89`
- Test: `tests/SqlAgent.Tests/ProviderIntegrationTests.cs`

**Interfaces:**
- Consumes: `SchemaModel.Build(..., views:)` from Task 3.
- Produces: `SqlServerProvider.GetSchemaAsync` returns a schema whose `ViewList` holds every user view.

- [ ] **Step 1: Write the failing integration test**

Add to `tests/SqlAgent.Tests/ProviderIntegrationTests.cs`. Put the DDL constant beside the existing `FixtureDdl`:

```csharp
    private const string SqlServerViewFixtureDdl = """
        IF OBJECT_ID('dbo.cd_c1_summary', 'V') IS NOT NULL DROP VIEW dbo.cd_c1_summary;
        IF OBJECT_ID('dbo.cd_c1_order', 'U') IS NOT NULL DROP TABLE dbo.cd_c1_order;
        CREATE TABLE dbo.cd_c1_order (id int NOT NULL PRIMARY KEY, sku varchar(20) NOT NULL, qty int NULL);
        EXEC('CREATE VIEW dbo.cd_c1_summary AS SELECT id, sku FROM dbo.cd_c1_order');
        """;
```

and the test:

```csharp
    [Fact]
    public async Task SqlServer_schema_extraction_separates_views_from_base_tables()
    {
        var connectionString = Environment.GetEnvironmentVariable(SqlServerConnectionStringEnv);
        if (string.IsNullOrWhiteSpace(connectionString))
            return;

        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();
        try
        {
            await using (var ddl = new SqlCommand(SqlServerViewFixtureDdl, conn))
                await ddl.ExecuteNonQueryAsync();

            var schema = await new SqlServerProvider().GetSchemaAsync(connectionString);

            // The view is a view, not a table. Before this phase the catalog query filtered on
            // TABLE_TYPE = 'BASE TABLE', so it appeared as neither.
            Assert.DoesNotContain(schema.Tables, t => t.Name == "cd_c1_summary");
            var view = Assert.Single(schema.ViewList, v => v.Name == "cd_c1_summary");
            Assert.Equal("dbo", view.Schema);
            Assert.Equal(["id", "sku"], view.Columns.Select(c => c.Name));
            // Column facets come from the same catalog the table columns come from, so sizing survives.
            Assert.Equal("varchar(20)", view.Columns.Single(c => c.Name == "sku").TypeText);

            // The base table is still a table and still complete.
            var table = Assert.Single(schema.Tables, t => t.Name == "cd_c1_order");
            Assert.Equal(["id"], table.PrimaryKey);
        }
        finally
        {
            await using var cleanup = new SqlCommand(
                "IF OBJECT_ID('dbo.cd_c1_summary', 'V') IS NOT NULL DROP VIEW dbo.cd_c1_summary; " +
                "IF OBJECT_ID('dbo.cd_c1_order', 'U') IS NOT NULL DROP TABLE dbo.cd_c1_order;", conn);
            await cleanup.ExecuteNonQueryAsync();
        }
    }
```

- [ ] **Step 2: Run it**

Run: `dotnet test tests/SqlAgent.Tests/SqlAgent.Tests.csproj --filter "FullyQualifiedName~SqlServer_schema_extraction_separates_views"`
Expected: PASS trivially if `SQLAGENT_TEST_SQLSERVER` is unset — the test returns before asserting anything. That is the existing opt-in convention. If you have a fixture, set the variable and watch it FAIL on `Assert.Single(schema.ViewList, ...)`.

- [ ] **Step 3: Split the column query by object type**

In `src/SqlAgent.Providers.SqlServer/SqlServerProvider.cs`, replace the `columns` query and add a `views` query beside it:

```csharp
        // One query shape, two object types. INFORMATION_SCHEMA.COLUMNS covers views as well as base
        // tables, so the discriminator on TABLE_TYPE is the only thing that separates them — which is
        // why this is a split rather than the filter it used to be.
        const string columnSelect = """
            SELECT c.TABLE_SCHEMA, c.TABLE_NAME, c.COLUMN_NAME, c.DATA_TYPE, c.IS_NULLABLE,
                   c.CHARACTER_MAXIMUM_LENGTH, c.NUMERIC_PRECISION, c.NUMERIC_SCALE
            FROM INFORMATION_SCHEMA.COLUMNS c
            JOIN INFORMATION_SCHEMA.TABLES t
              ON t.TABLE_SCHEMA = c.TABLE_SCHEMA AND t.TABLE_NAME = c.TABLE_NAME
            WHERE t.TABLE_TYPE =
            """;

        var columns = await Query(conn, ct,
            $"""
            {columnSelect} 'BASE TABLE'
            ORDER BY c.TABLE_SCHEMA, c.TABLE_NAME, c.ORDINAL_POSITION
            """,
            r => (r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3),
                  string.Equals(r.GetString(4), "YES", StringComparison.OrdinalIgnoreCase),
                  NullableInt(r, 5), NullableInt(r, 6), NullableInt(r, 7)));

        var views = await Query(conn, ct,
            $"""
            {columnSelect} 'VIEW'
            ORDER BY c.TABLE_SCHEMA, c.TABLE_NAME, c.ORDINAL_POSITION
            """,
            r => (r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3),
                  string.Equals(r.GetString(4), "YES", StringComparison.OrdinalIgnoreCase),
                  NullableInt(r, 5), NullableInt(r, 6), NullableInt(r, 7)));
```

Then change the return at the end of `GetSchemaAsync`:

```csharp
        return SchemaModel.Build(columns, pks, fks, indexes, views);
```

- [ ] **Step 4: Build and run the suite**

Run: `dotnet build && dotnet test tests/SqlAgent.Tests/SqlAgent.Tests.csproj`
Expected: PASS. With no fixture configured the new test is a no-op; the build is what this step actually proves.

- [ ] **Step 5: Commit**

```bash
git add src/SqlAgent.Providers.SqlServer/SqlServerProvider.cs tests/SqlAgent.Tests/ProviderIntegrationTests.cs
git commit -m "Extract views from SQL Server"
```

---

## Task 5: Extract views from Postgres

**Files:**
- Modify: `src/SqlAgent.Providers.Postgres/PostgresProvider.cs:37-49,102`
- Test: `tests/SqlAgent.Tests/ProviderIntegrationTests.cs`

**Interfaces:**
- Consumes: `SchemaModel.Build(..., views:)` from Task 3.
- Produces: `PostgresProvider.GetSchemaAsync` returns a schema whose `ViewList` holds every user view.

- [ ] **Step 1: Write the failing integration test**

Add to `tests/SqlAgent.Tests/ProviderIntegrationTests.cs`:

```csharp
    private const string PostgresViewFixtureDdl = """
        DROP SCHEMA IF EXISTS "cd_c1" CASCADE;
        CREATE SCHEMA "cd_c1";
        CREATE TABLE "cd_c1"."order" (id integer PRIMARY KEY, sku varchar(20) NOT NULL, qty integer);
        CREATE VIEW "cd_c1"."summary" AS SELECT id, sku FROM "cd_c1"."order";
        CREATE MATERIALIZED VIEW "cd_c1"."rollup" AS SELECT count(*) AS n FROM "cd_c1"."order";
        """;

    [Fact]
    public async Task Postgres_schema_extraction_separates_views_and_ignores_materialized_ones()
    {
        var connectionString = Environment.GetEnvironmentVariable(PostgresConnectionStringEnv);
        if (string.IsNullOrWhiteSpace(connectionString))
            return;

        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        try
        {
            await using (var ddl = new NpgsqlCommand(PostgresViewFixtureDdl, conn))
                await ddl.ExecuteNonQueryAsync();

            var schema = await new PostgresProvider().GetSchemaAsync(connectionString);

            Assert.DoesNotContain(schema.Tables, t => t.Name == "summary");
            var view = Assert.Single(schema.ViewList, v => v.Name == "summary");
            Assert.Equal("cd_c1", view.Schema);
            Assert.Equal(["id", "sku"], view.Columns.Select(c => c.Name));
            Assert.Equal("character varying(20)", view.Columns.Single(c => c.Name == "sku").TypeText);

            // Materialized views are out of scope for this phase, and information_schema does not list
            // them at all — this asserts the boundary rather than assuming it.
            Assert.DoesNotContain(schema.ViewList, v => v.Name == "rollup");
            Assert.DoesNotContain(schema.Tables, t => t.Name == "rollup");

            var table = Assert.Single(schema.Tables, t => t.Name == "order");
            Assert.Equal(["id"], table.PrimaryKey);
        }
        finally
        {
            await using var cleanup = new NpgsqlCommand("DROP SCHEMA IF EXISTS \"cd_c1\" CASCADE;", conn);
            await cleanup.ExecuteNonQueryAsync();
        }
    }
```

- [ ] **Step 2: Run it**

Run: `dotnet test tests/SqlAgent.Tests/SqlAgent.Tests.csproj --filter "FullyQualifiedName~Postgres_schema_extraction_separates_views"`
Expected: PASS trivially without `SQLAGENT_TEST_POSTGRES`; FAIL on `Assert.Single(schema.ViewList, ...)` with a fixture.

- [ ] **Step 3: Split the column query by table type**

In `src/SqlAgent.Providers.Postgres/PostgresProvider.cs`, replace the `columns` query with the pair:

```csharp
        // One query shape, two object types. information_schema.columns covers views as well as base
        // tables, and information_schema.tables reports a plain view as 'VIEW' — so the discriminator is
        // a split rather than the filter it used to be. A materialized view appears in neither catalog,
        // which is how they stay out of scope without a special case here.
        const string columnSelect = """
            SELECT c.table_schema, c.table_name, c.column_name, c.data_type, c.is_nullable,
                   c.character_maximum_length, c.numeric_precision, c.numeric_scale
            FROM information_schema.columns c
            JOIN information_schema.tables t
              ON t.table_schema = c.table_schema AND t.table_name = c.table_name
            WHERE t.table_type =
            """;

        var columns = await Query(conn, ct,
            $"""
            {columnSelect} 'BASE TABLE' AND {userSchemas}
            ORDER BY c.table_schema, c.table_name, c.ordinal_position
            """,
            r => (r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3),
                  string.Equals(r.GetString(4), "YES", StringComparison.OrdinalIgnoreCase),
                  NullableInt(r, 5), NullableInt(r, 6), NullableInt(r, 7)));

        var views = await Query(conn, ct,
            $"""
            {columnSelect} 'VIEW' AND {userSchemas}
            ORDER BY c.table_schema, c.table_name, c.ordinal_position
            """,
            r => (r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3),
                  string.Equals(r.GetString(4), "YES", StringComparison.OrdinalIgnoreCase),
                  NullableInt(r, 5), NullableInt(r, 6), NullableInt(r, 7)));
```

Then change the return at the end of `GetSchemaAsync`:

```csharp
        return SchemaModel.Build(columns, pks, fks, indexes, views);
```

Note the `userSchemas` constant is already declared above the queries and already excludes `pg_%` and `information_schema`; it is reused verbatim so views obey exactly the same system-schema exclusion tables do.

- [ ] **Step 4: Build and run the suite**

Run: `dotnet build && dotnet test tests/SqlAgent.Tests/SqlAgent.Tests.csproj`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/SqlAgent.Providers.Postgres/PostgresProvider.cs tests/SqlAgent.Tests/ProviderIntegrationTests.cs
git commit -m "Extract views from Postgres"
```

---

## Task 6: Tell a write target from a read source

Without this, `INSERT INTO writable SELECT * FROM readonly_lookup` is indistinguishable from
`INSERT INTO readonly_lookup SELECT * FROM writable`, and the Read-only level would have to forbid
reading a lookup table inside any write to stay closed.

**Files:**
- Modify: `src/SqlAgent.Core/Policy/SqlPolicy.cs`
- Test: `tests/SqlAgent.Tests/WriteTargetTests.cs` (create)

**Interfaces:**
- Produces, used by Task 7:
  - `record ParsedStatement(SqlStatementKind Kind, string StatementType, string Normalized, IReadOnlyList<SqlTableReference> Tables, IReadOnlyList<SqlTableReference> WrittenTables)`
  - `Tables` keeps its present meaning: every object the statement touches, write targets included.
  - For a `Write` statement, `WrittenTables` is never empty — if the target cannot be identified it falls back to every referenced table.

- [ ] **Step 1: Write the failing tests**

Create `tests/SqlAgent.Tests/WriteTargetTests.cs`:

```csharp
using SqlAgent.Core;
using SqlAgent.Core.Policy;

namespace SqlAgent.Tests;

/// <summary>
/// The parser's flat table list cannot say which object a statement writes to, and per-object access
/// needs exactly that. These tests pin the split from the outside — what is written, what is merely read
/// — across both dialects and across the forms that hide the target behind other syntax.
/// </summary>
public class WriteTargetTests
{
    private static ParsedStatement Parse(string sql, DatabaseProviderType provider = DatabaseProviderType.Postgres)
        => Assert.Single(SqlAnalyzer.Analyze(sql, provider));

    private static string[] Names(IEnumerable<SqlTableReference> refs)
        => refs.Select(r => r.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();

    [Fact]
    public void A_select_writes_to_nothing()
    {
        var stmt = Parse("SELECT id FROM orders JOIN customers ON customers.id = orders.customer_id");

        Assert.Empty(stmt.WrittenTables);
        Assert.Equal(["customers", "orders"], Names(stmt.Tables));
    }

    [Fact]
    public void Insert_writes_only_to_its_target()
    {
        var stmt = Parse("INSERT INTO orders (id) VALUES (1)");

        Assert.Equal(["orders"], Names(stmt.WrittenTables));
    }

    [Fact]
    public void Insert_select_writes_to_the_target_and_reads_the_source()
    {
        // The case the flat list could not express, and the reason the Read-only level is worth having:
        // copying out of a lookup table into a writable one must not be refused.
        var stmt = Parse("INSERT INTO orders (id) SELECT id FROM staging_orders");

        Assert.Equal(["orders"], Names(stmt.WrittenTables));
        Assert.Equal(["orders", "staging_orders"], Names(stmt.Tables));
    }

    [Theory]
    [InlineData(DatabaseProviderType.Postgres)]
    [InlineData(DatabaseProviderType.SqlServer)]
    public void Update_from_writes_to_the_target_and_reads_the_from_clause(DatabaseProviderType provider)
    {
        var stmt = Parse(
            "UPDATE orders SET total = s.total FROM staging_orders s WHERE s.id = orders.id", provider);

        Assert.Equal(["orders"], Names(stmt.WrittenTables));
        Assert.Contains("staging_orders", Names(stmt.Tables));
    }

    [Fact]
    public void Update_with_a_subquery_does_not_treat_the_subquery_source_as_written()
    {
        var stmt = Parse("UPDATE orders SET total = (SELECT max(amount) FROM payments)");

        Assert.Equal(["orders"], Names(stmt.WrittenTables));
        Assert.Equal(["orders", "payments"], Names(stmt.Tables));
    }

    [Theory]
    [InlineData(DatabaseProviderType.Postgres)]
    [InlineData(DatabaseProviderType.SqlServer)]
    public void Delete_writes_to_its_target(DatabaseProviderType provider)
    {
        var stmt = Parse("DELETE FROM orders WHERE id = 1", provider);

        Assert.Equal(["orders"], Names(stmt.WrittenTables));
    }

    [Fact]
    public void Delete_with_a_subquery_does_not_treat_the_subquery_source_as_written()
    {
        var stmt = Parse("DELETE FROM orders WHERE id IN (SELECT order_id FROM refunds)");

        Assert.Equal(["orders"], Names(stmt.WrittenTables));
        Assert.Equal(["orders", "refunds"], Names(stmt.Tables));
    }

    [Fact]
    public void A_write_target_named_by_schema_keeps_its_schema()
    {
        var stmt = Parse("UPDATE sales.orders SET total = 0");

        var target = Assert.Single(stmt.WrittenTables);
        Assert.Equal("sales", target.Schema);
        Assert.Equal("orders", target.Name);
    }

    [Fact]
    public void A_cte_alias_is_not_a_write_target()
    {
        // The CTE name is not an object, so it must reach neither list — the same rule the visibility
        // check has always applied, now also on the write side.
        var stmt = Parse("WITH recent AS (SELECT id FROM orders) INSERT INTO archive SELECT id FROM recent");

        Assert.Equal(["archive"], Names(stmt.WrittenTables));
        Assert.DoesNotContain("recent", Names(stmt.Tables));
    }

    [Fact]
    public void A_write_whose_target_cannot_be_identified_treats_every_reference_as_written()
    {
        // The fail-closed fallback, asserted through the invariant rather than by breaking the parser:
        // a Write statement must never report an empty write set, because an empty one would mean the
        // per-object check silently passes on a statement that does modify something.
        foreach (var sql in new[]
        {
            "INSERT INTO orders (id) VALUES (1)",
            "UPDATE orders SET total = 0",
            "DELETE FROM orders",
        })
        {
            var stmt = Parse(sql);
            Assert.Equal(SqlStatementKind.Write, stmt.Kind);
            Assert.NotEmpty(stmt.WrittenTables);
        }
    }

    [Fact]
    public void Every_written_table_is_also_a_referenced_table()
    {
        // Tables keeps its old meaning, so the visibility check does not change behaviour: a write target
        // is still checked for visibility exactly as before.
        var stmt = Parse("UPDATE orders SET total = s.total FROM staging_orders s");

        foreach (var written in stmt.WrittenTables)
            Assert.Contains(written, stmt.Tables);
    }
}
```

- [ ] **Step 2: Run them and watch them fail**

Run: `dotnet test tests/SqlAgent.Tests/SqlAgent.Tests.csproj --filter "FullyQualifiedName~WriteTargetTests"`
Expected: FAIL to compile — `'ParsedStatement' does not contain a definition for 'WrittenTables'`.

- [ ] **Step 3: Discover how this SqlParserCS version names the targets**

Do not guess the property names. `Statement.Insert` already reaches its target through `insert.InsertOperation.Name` rather than a bare property, so this version keeps at least some statement bodies in nested operation records, and `UPDATE`/`DELETE` may or may not do the same.

Create a throwaway `tests/SqlAgent.Tests/AstShapeProbe.cs`:

```csharp
using System.Reflection;
using SqlAgent.Core;
using SqlAgent.Core.Policy;
using SqlParser;
using SqlParser.Dialects;
using Xunit.Abstractions;

namespace SqlAgent.Tests;

public class AstShapeProbe(ITestOutputHelper output)
{
    [Theory]
    [InlineData("UPDATE orders SET total = 0 FROM staging s")]
    [InlineData("DELETE FROM orders WHERE id = 1")]
    [InlineData("INSERT INTO orders (id) VALUES (1)")]
    public void Dump(string sql)
    {
        var statement = new Parser().ParseSql(sql, new PostgreSqlDialect()).Single();
        Dump(statement, 0);

        void Dump(object node, int depth)
        {
            if (depth > 3) return;
            output.WriteLine($"{new string(' ', depth * 2)}{node.GetType().FullName}");
            foreach (var p in node.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (p.GetIndexParameters().Length > 0) continue;
                object? v;
                try { v = p.GetValue(node); } catch { continue; }
                output.WriteLine($"{new string(' ', depth * 2 + 2)}.{p.Name} = {v?.GetType().Name ?? "null"}");
                if (v is not null && v.GetType().Namespace?.StartsWith("SqlParser.Ast", StringComparison.Ordinal) == true)
                    Dump(v, depth + 2);
            }
        }
    }
}
```

Run: `dotnet test tests/SqlAgent.Tests/SqlAgent.Tests.csproj --filter "FullyQualifiedName~AstShapeProbe" --logger "console;verbosity=detailed"`

Write down, from the output: which property on `Statement.Update` holds the target relation, and which property on `Statement.Delete` (or on a nested operation record it exposes) holds the `FROM` target. Those two names are the only thing Step 4 needs.

- [ ] **Step 4: Split the collector's output**

In `src/SqlAgent.Core/Policy/SqlPolicy.cs`, replace the `ParsedStatement` record:

```csharp
/// <summary>
/// One parsed statement: its kind, the parser's concrete type name, its canonical re-rendered
/// (normalized) SQL, every table it touches, and the subset of those it writes to.
/// <paramref name="WrittenTables"/> is always a subset of <paramref name="Tables"/>, so a visibility
/// check over <paramref name="Tables"/> still sees write targets exactly as it did before this existed.
/// For a <see cref="SqlStatementKind.Write"/> statement it is never empty — see
/// <see cref="SqlAnalyzer"/> for why an unidentifiable target falls back to everything.
/// </summary>
public record ParsedStatement(
    SqlStatementKind Kind,
    string StatementType,
    string Normalized,
    IReadOnlyList<SqlTableReference> Tables,
    IReadOnlyList<SqlTableReference> WrittenTables);
```

Replace `SqlAnalyzer.Describe`:

```csharp
    private static ParsedStatement Describe(Statement statement)
    {
        var collector = new TableCollector();
        collector.Walk(statement, ImmutableHashSet<string>.Empty.WithComparer(StringComparer.OrdinalIgnoreCase));

        var kind = statement switch
        {
            Statement.Select => SqlStatementKind.Read,
            Statement.Insert or Statement.Update or Statement.Delete => SqlStatementKind.Write,
            _ => SqlStatementKind.Other,
        };

        // Fail closed. The collector finds a write target by walking the AST property that holds it, and
        // an upgrade of SqlParserCS that renames or restructures that property would leave the set empty
        // — which the per-object check would read as "this statement writes to nothing" and wave through.
        // Falling back to every referenced table makes the same upgrade over-strict instead: a write
        // touching a Read-only object is refused, which is loud, recoverable, and the right direction.
        var written = collector.WrittenReferences;
        if (kind == SqlStatementKind.Write && written.Count == 0)
            written = collector.References;

        return new ParsedStatement(
            kind, statement.GetType().Name, statement.ToSql(), collector.References, written);
    }
```

In `TableCollector`, add the written set beside the existing one:

```csharp
        private readonly List<SqlTableReference> _refs = [];
        private readonly List<SqlTableReference> _written = [];
        private readonly HashSet<(string?, string)> _seen = [];
        private readonly HashSet<(string?, string)> _seenWritten = [];
        private readonly HashSet<object> _visited = new(ReferenceEqualityComparer.Instance);

        public IReadOnlyList<SqlTableReference> References => _refs;
        public IReadOnlyList<SqlTableReference> WrittenReferences => _written;
```

Thread a flag through `Walk`, and walk the target-bearing property first so the visited-set guard cannot see a target as a read source before it sees it as a write target:

```csharp
        public void Walk(object? node, ImmutableHashSet<string> scope, bool writeTarget = false)
        {
            if (node is null or string) return;
            if (node is IEnumerable sequence)
            {
                foreach (var item in sequence) Walk(item, scope, writeTarget);
                return;
            }
            if (node.GetType().Namespace?.StartsWith("SqlParser.Ast", StringComparison.Ordinal) != true) return;
            if (!_visited.Add(node)) return;

            // Real table references: FROM/JOIN targets, plus UPDATE/DELETE targets (also TableFactor.Table).
            if (node is TableFactor.Table table)
                AddUnlessCte(table.Name, scope, writeTarget);
            // INSERT target is a bare ObjectName, not a TableFactor, so pick it up explicitly — and it is
            // unambiguously the write target, whatever flag the walk arrived with.
            else if (node is Statement.Insert insert)
                AddUnlessCte(insert.InsertOperation.Name, scope, writeTarget: true);

            // A WITH clause needs per-part scoping, so handle a Query's CTEs explicitly rather than letting
            // the generic property walk apply one flat scope to both the bodies and the CTE definitions.
            if (node is Query { With: { } with } query)
            {
                WalkQueryWithCtes(query, with, scope);
                return;
            }

            // UPDATE and DELETE name their target as an ordinary TableFactor, indistinguishable from the
            // read sources beside it in a FROM or USING clause. The only thing that separates them is
            // which property of the statement they hang off, so that property is walked first with the
            // flag set; _visited then keeps the generic walk below from revisiting it as a read source.
            // Walking the whole subtree with the flag on is deliberate: a joined relation under an UPDATE
            // target is not valid in either supported dialect, and if one ever appears, counting it as
            // written is the fail-closed answer.
            foreach (var target in WriteTargets(node))
                Walk(target, scope, writeTarget: true);

            foreach (var value in ChildNodes(node))
                Walk(value, scope, writeTarget);
        }

        /// <summary>
        /// The property (or properties) holding a statement's write target, by name. Reflection rather
        /// than a type test because SqlParserCS offers no marker for "this is the thing being modified",
        /// and the names differ between an UPDATE and a DELETE. A name that stops matching after a
        /// package upgrade yields nothing here, which <see cref="SqlAnalyzer.Describe"/> turns into the
        /// fail-closed fallback rather than into silent permission.
        /// </summary>
        private static IEnumerable<object?> WriteTargets(object node)
        {
            // Replace these two name lists with what the Step 3 probe actually printed for this version.
            var names = node switch
            {
                Statement.Update => new[] { "Table" },
                Statement.Delete => new[] { "From", "FromTable", "Tables" },
                _ => [],
            };

            foreach (var name in names)
            {
                var prop = node.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                if (prop is null || prop.GetIndexParameters().Length > 0) continue;
                object? value;
                try { value = prop.GetValue(node); }
                catch { continue; }
                if (value is not null) yield return value;
            }
        }
```

and record the write side in `AddUnlessCte`:

```csharp
        private void AddUnlessCte(ObjectName name, ImmutableHashSet<string> scope, bool writeTarget)
        {
            // Identifier values are already unquoted ([dbo].[T] -> dbo, T). Last part is the table, the
            // part before it (if any) is the schema; deeper qualifiers (db.schema.table) are ignored.
            var parts = name.Values.Select(i => i.Value).ToList();
            if (parts.Count == 0) return;
            var tableName = parts[^1];
            var schema = parts.Count >= 2 ? parts[^2] : null;

            // Unqualified name matching an in-scope CTE is an alias, not a table — skip it. This applies
            // to the write side too: INSERT INTO a CTE name is not a write to an object.
            if (schema is null && scope.Contains(tableName)) return;

            var reference = new SqlTableReference(schema, tableName);
            if (_seen.Add((schema, tableName)))
                _refs.Add(reference);
            if (writeTarget && _seenWritten.Add((schema, tableName)))
                _written.Add(reference);
        }
```

`WalkQueryWithCtes` calls `Walk(cte.Query, ...)` and walks the remaining properties — leave both at the default `writeTarget: false`. A CTE body is always a read.

- [ ] **Step 5: Delete the probe**

```bash
rm tests/SqlAgent.Tests/AstShapeProbe.cs
```

It was a measuring instrument, not a test. Leaving it behind means a permanently failing-to-be-useful test that prints AST dumps into every CI run.

- [ ] **Step 6: Run the tests and watch them pass**

Run: `dotnet test tests/SqlAgent.Tests/SqlAgent.Tests.csproj --filter "FullyQualifiedName~WriteTargetTests"`
Expected: PASS, 13 test cases.

If `Update_from_...` or `Delete_...` still shows the target missing from `WrittenTables`, the name list in `WriteTargets` does not match this version — go back to the probe output rather than widening the list speculatively. If a test shows a read source appearing in `WrittenTables`, the property being walked is too broad: narrow it to the target relation only.

- [ ] **Step 7: Run the full suite**

Run: `dotnet test tests/SqlAgent.Tests/SqlAgent.Tests.csproj`
Expected: PASS. `SqlPolicyValidatorTests` is unaffected — `Tables` is unchanged and nothing reads `WrittenTables` yet.

- [ ] **Step 8: Commit**

```bash
git add src/SqlAgent.Core/Policy/SqlPolicy.cs tests/SqlAgent.Tests/WriteTargetTests.cs
git commit -m "Tell a statement's write target from its read sources"
```

---

## Task 7: One access resolver in place of the visibility predicate

**Files:**
- Modify: `src/SqlAgent.Core/Policy/SqlPolicy.cs`
- Modify: `src/SqlAgent.Storage/QueryExecutionService.cs:30-31,85-101`
- Test: `tests/SqlAgent.Tests/SqlPolicyValidatorTests.cs`

**Interfaces:**
- Consumes: `ParsedStatement.WrittenTables` from Task 6.
- Produces, used by Tasks 9 and 10:
  - `enum ObjectAccess { Hidden, ReadOnly, Full }`
  - `record ObjectPolicy(ObjectAccess Access, bool IsView)` with `static ObjectPolicy Default => new(ObjectAccess.Full, false)`
  - `SqlPolicyValidator.Validate(string sql, DatabaseProviderType provider, bool isReadOnly, Func<SqlTableReference, ObjectPolicy> resolve)`
  - New deny codes `policy_denied_view_write` and `policy_denied_readonly_object`.

This task changes behaviour for nobody: `QueryExecutionService` is updated to supply a resolver that returns `Hidden` or `Full` from the same hidden-table query it runs today, and never reports a view. Task 10 is what gives it levels and views.

- [ ] **Step 1: Write the failing tests**

In `tests/SqlAgent.Tests/SqlPolicyValidatorTests.cs`, replace the two helpers at the top of the class:

```csharp
    // Default: every object fully accessible. Individual tests override.
    private static Func<SqlTableReference, ObjectPolicy> Policy(
        string[]? hidden = null, string[]? readOnly = null, string[]? views = null)
    {
        var hiddenSet = (hidden ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var readOnlySet = (readOnly ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var viewSet = (views ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);

        return t =>
        {
            bool Match(HashSet<string> set) => set.Contains(t.Name) || set.Contains(t.ToString());
            var access = Match(hiddenSet) ? ObjectAccess.Hidden
                : Match(readOnlySet) ? ObjectAccess.ReadOnly
                : ObjectAccess.Full;
            return new ObjectPolicy(access, Match(viewSet));
        };
    }

    private static PolicyDecision Validate(
        string sql,
        bool isReadOnly = false,
        DatabaseProviderType provider = DatabaseProviderType.Postgres,
        Func<SqlTableReference, ObjectPolicy>? resolve = null)
        => SqlPolicyValidator.Validate(sql, provider, isReadOnly, resolve ?? Policy());
```

Update the existing hidden-table tests in that file to call `Policy(hidden: [...])` wherever they call `Visible(...)` today — the assertions themselves do not change.

Then add a new region at the end of the class:

```csharp
    // --- Per-object access (Phase C1) ------------------------------------------

    [Fact]
    public void A_write_to_a_read_only_object_is_denied()
    {
        var d = Validate("UPDATE orders SET total = 0", resolve: Policy(readOnly: ["orders"]));

        Assert.False(d.Allowed);
        Assert.Equal("policy_denied_readonly_object", d.DenyCode);
        Assert.Contains("orders", d.Reason);
    }

    [Fact]
    public void A_read_from_a_read_only_object_is_allowed()
    {
        var d = Validate("SELECT id FROM orders", resolve: Policy(readOnly: ["orders"]));

        Assert.True(d.Allowed);
    }

    [Fact]
    public void Reading_a_read_only_object_inside_a_write_is_allowed()
    {
        // The reason write targets had to be separated at all. A lookup table nobody may modify is still
        // a lookup table: copying out of it into a writable target is an ordinary thing to want.
        var d = Validate(
            "INSERT INTO orders (id) SELECT id FROM staging_orders",
            resolve: Policy(readOnly: ["staging_orders"]));

        Assert.True(d.Allowed);
    }

    [Fact]
    public void A_write_to_a_view_is_denied_as_a_view_write_not_as_a_level()
    {
        // Ordering, asserted directly. A view can only be Not visible or Read-only, so if the level check
        // ran first this code would be unreachable and the message would tell the user to change a level
        // that cannot be changed.
        var d = Validate(
            "UPDATE order_summary SET total = 0",
            resolve: Policy(readOnly: ["order_summary"], views: ["order_summary"]));

        Assert.False(d.Allowed);
        Assert.Equal("policy_denied_view_write", d.DenyCode);
    }

    [Fact]
    public void A_read_from_a_view_is_allowed()
    {
        var d = Validate("SELECT id FROM order_summary", resolve: Policy(views: ["order_summary"]));

        Assert.True(d.Allowed);
    }

    [Fact]
    public void A_hidden_object_is_still_denied_as_hidden_even_when_it_is_written_to()
    {
        // Visibility outranks both new checks: a hidden object's very name must not leak, and
        // "you may not write to it" concedes that it exists.
        var d = Validate(
            "UPDATE orders SET total = 0",
            resolve: Policy(hidden: ["orders"], readOnly: ["orders"], views: ["orders"]));

        Assert.False(d.Allowed);
        Assert.Equal("policy_denied_hidden_table", d.DenyCode);
    }

    [Fact]
    public void The_connection_read_only_flag_still_outranks_per_object_access()
    {
        var d = Validate("UPDATE orders SET total = 0", isReadOnly: true, resolve: Policy());

        Assert.False(d.Allowed);
        Assert.Equal("policy_denied_readonly", d.DenyCode);
    }

    [Fact]
    public void An_object_with_default_policy_is_writable()
    {
        // The phase's central decision, pinned where it is enforced: absence means full access.
        var d = Validate("DELETE FROM orders WHERE id = 1", resolve: Policy());

        Assert.True(d.Allowed);
    }

    [Fact]
    public void An_unqualified_name_takes_the_most_restrictive_level_of_its_matches()
    {
        // Same fail-closed rule the hidden check has always used, now over three levels: a bare name that
        // matches a read-only object in some schema is treated as read-only.
        var resolve = (SqlTableReference t) => t.Schema is null
            ? new ObjectPolicy(ObjectAccess.ReadOnly, false)
            : new ObjectPolicy(ObjectAccess.Full, false);

        var d = Validate("UPDATE orders SET total = 0", resolve: resolve);

        Assert.False(d.Allowed);
        Assert.Equal("policy_denied_readonly_object", d.DenyCode);
    }
```

- [ ] **Step 2: Run them and watch them fail**

Run: `dotnet test tests/SqlAgent.Tests/SqlAgent.Tests.csproj --filter "FullyQualifiedName~SqlPolicyValidatorTests"`
Expected: FAIL to compile — `ObjectPolicy` and `ObjectAccess` do not exist, and `Validate` has no overload taking a resolver.

- [ ] **Step 3: Add the access types**

In `src/SqlAgent.Core/Policy/SqlPolicy.cs`, above `SqlPolicyValidator`:

```csharp
/// <summary>
/// What a connection's policy allows against one object. Ordered from most to least restrictive so a
/// caller resolving an ambiguous (unqualified) name can take the minimum and stay fail-closed.
/// </summary>
public enum ObjectAccess
{
    /// <summary>Not visible: absent from the schema handed to the model, and refused if named anyway.</summary>
    Hidden = 0,

    /// <summary>Visible and readable; refused as the target of a write.</summary>
    ReadOnly = 1,

    /// <summary>Visible, readable, writable. Also what an object with no policy row gets.</summary>
    Full = 2,
}

/// <summary>
/// One object's effective policy: its access level, and whether it is a view. Both travel together
/// because a caller resolving a name has to do exactly one lookup, and doing it twice is how the
/// fail-closed matching rule for unqualified names ends up implemented two slightly different ways.
/// </summary>
public record ObjectPolicy(ObjectAccess Access, bool IsView)
{
    /// <summary>An object with no policy row: fully accessible, and not a view until something says so.</summary>
    public static ObjectPolicy Default { get; } = new(ObjectAccess.Full, false);
}
```

- [ ] **Step 4: Rewrite Validate around the resolver**

Replace `SqlPolicyValidator` entirely:

```csharp
/// <summary>
/// Applies connection policy to parsed SQL (CD-50 T5): rejects multi-statement batches, unsupported
/// statements, mutating statements on read-only connections, references to hidden objects, writes to
/// views, and writes to read-only objects — all before execution. Decoupled from storage: policy is
/// supplied as one resolver.
/// </summary>
public static class SqlPolicyValidator
{
    /// <param name="resolve">
    /// Returns the effective policy for one referenced object. An object with no policy row must resolve
    /// to <see cref="ObjectPolicy.Default"/> — absence means full access, which is the rule every layer
    /// in this codebase applies. Only real objects reach this resolver: CTE aliases are resolved out
    /// scope-aware during parsing, while the real base tables inside a CTE body are still passed in, so a
    /// hidden object cannot be masked by wrapping it in a CTE or alias.
    ///
    /// The caller owns the fail-closed rule for an unqualified name — take the most restrictive
    /// <see cref="ObjectAccess"/> among same-named objects across schemas, and report a view if any match
    /// is one.
    /// </param>
    public static PolicyDecision Validate(
        string sql,
        DatabaseProviderType provider,
        bool isReadOnly,
        Func<SqlTableReference, ObjectPolicy> resolve)
    {
        if (string.IsNullOrWhiteSpace(sql))
            return PolicyDecision.Deny("policy_denied_empty", "No executable SQL statement was provided.", []);

        IReadOnlyList<ParsedStatement> statements;
        try
        {
            statements = SqlAnalyzer.Analyze(sql, provider);
        }
        catch (ParserException ex)
        {
            return PolicyDecision.Deny("policy_denied_parse_error", $"SQL could not be parsed: {ex.Message}", []);
        }

        if (statements.Count == 0)
            return PolicyDecision.Deny("policy_denied_empty", "No executable SQL statement was provided.", []);

        if (statements.Count > 1)
        {
            var all = statements.SelectMany(s => s.Tables).ToList();
            return PolicyDecision.Deny(
                "policy_denied_multi_statement",
                $"Multi-statement batches are not allowed ({statements.Count} statements found).",
                all);
        }

        var stmt = statements[0];

        if (stmt.Kind == SqlStatementKind.Other)
            return PolicyDecision.Deny(
                "policy_denied_unsupported",
                $"Statement type '{stmt.StatementType}' is not supported.",
                stmt.Tables, stmt.Normalized);

        if (isReadOnly && stmt.Kind != SqlStatementKind.Read)
            return PolicyDecision.Deny(
                "policy_denied_readonly",
                $"Connection is read-only; '{stmt.StatementType}' would modify data.",
                stmt.Tables, stmt.Normalized);

        // Visibility first, and over every reference rather than only the written ones. A hidden object's
        // name must not leak, and refusing it for any more specific reason would concede that it exists.
        var hidden = stmt.Tables.Where(t => resolve(t).Access == ObjectAccess.Hidden).ToList();
        if (hidden.Count > 0)
            return PolicyDecision.Deny(
                "policy_denied_hidden_table",
                $"References table(s) not visible to this connection: {string.Join(", ", hidden)}.",
                stmt.Tables, stmt.Normalized);

        // Views before levels. A view can only be Not visible or Read-only, so checking the level first
        // would make this branch unreachable and would tell the user to raise a level that the object is
        // not allowed to have.
        var writtenViews = stmt.WrittenTables.Where(t => resolve(t).IsView).ToList();
        if (writtenViews.Count > 0)
            return PolicyDecision.Deny(
                "policy_denied_view_write",
                $"'{stmt.StatementType}' writes to view(s): {string.Join(", ", writtenViews)}.",
                stmt.Tables, stmt.Normalized);

        var readOnlyTargets = stmt.WrittenTables
            .Where(t => resolve(t).Access == ObjectAccess.ReadOnly)
            .ToList();
        if (readOnlyTargets.Count > 0)
            return PolicyDecision.Deny(
                "policy_denied_readonly_object",
                $"'{stmt.StatementType}' writes to read-only object(s): {string.Join(", ", readOnlyTargets)}.",
                stmt.Tables, stmt.Normalized);

        return PolicyDecision.Allow(stmt.Tables, stmt.Normalized);
    }
}
```

- [ ] **Step 5: Keep QueryExecutionService compiling and behaving identically**

In `src/SqlAgent.Storage/QueryExecutionService.cs`, rename `BuildVisibilityAsync` to `BuildPolicyResolverAsync` and return the resolver. Behaviour is unchanged: the same hidden-table query, mapped to `Hidden` or `Full`, with no view ever reported. Task 10 replaces the body.

```csharp
        var resolve = await BuildPolicyResolverAsync(connectionId, ct);
        var decision = SqlPolicyValidator.Validate(sql, info.ProviderType, info.IsReadOnly, resolve);
```

```csharp
    /// <summary>
    /// An object is hidden if a TablePolicy marks it invisible. Matching is fail-closed for unqualified
    /// SQL: a bare name is hidden if any schema's same-named object is hidden; a schema-qualified name
    /// must match the policy's schema too. Objects with no policy row default to full access — the same
    /// rule the schema description path applies.
    ///
    /// Levels and views are not read here yet; Task 10 widens this. Until then every visible object
    /// resolves to Full and nothing resolves as a view, which is exactly the behaviour that shipped
    /// before per-object access existed.
    /// </summary>
    private async Task<Func<SqlTableReference, ObjectPolicy>> BuildPolicyResolverAsync(
        Guid connectionId, CancellationToken ct)
    {
        var hidden = await db.TablePolicies
            .Where(p => p.DatabaseConnectionId == connectionId && !p.IsVisible)
            .Select(p => new { p.SchemaName, p.TableName })
            .ToListAsync(ct);

        return t => hidden.Any(h =>
            string.Equals(h.TableName, t.Name, StringComparison.OrdinalIgnoreCase) &&
            (t.Schema is null || string.Equals(h.SchemaName, t.Schema, StringComparison.OrdinalIgnoreCase)))
                ? new ObjectPolicy(ObjectAccess.Hidden, false)
                : ObjectPolicy.Default;
    }
```

- [ ] **Step 6: Run the tests and watch them pass**

Run: `dotnet test tests/SqlAgent.Tests/SqlAgent.Tests.csproj --filter "FullyQualifiedName~SqlPolicyValidatorTests"`
Expected: PASS, including the nine new cases.

- [ ] **Step 7: Run the full suite**

Run: `dotnet test tests/SqlAgent.Tests/SqlAgent.Tests.csproj`
Expected: PASS. `QueryExecutionServiceTests` must be untouched — if any of its hidden-table tests now behave differently, the resolver is not equivalent to the predicate it replaced.

- [ ] **Step 8: Commit**

```bash
git add src/SqlAgent.Core/Policy/SqlPolicy.cs src/SqlAgent.Storage/QueryExecutionService.cs tests/SqlAgent.Tests/SqlPolicyValidatorTests.cs
git commit -m "Replace the visibility predicate with one access resolver"
```

---

## Task 8: Clear the schema cache so views can reach the model

Cached JSON written before Task 3 carries no views. Without this, a view would silently never reach the
model until some unrelated policy change happened to invalidate the cache.

**Files:**
- Create: `src/SqlAgent.Storage/Migrations/<timestamp>_ClearSchemaCacheForViews.cs` (generated, then edited)
- Test: `tests/SqlAgent.Tests/StoreMigrationTests.cs`

**Interfaces:**
- Produces: a migration id ending `_ClearSchemaCacheForViews`, applied by `StoreInitializer` like any other.

- [ ] **Step 1: Write the failing test**

Add to `tests/SqlAgent.Tests/StoreMigrationTests.cs`:

```csharp
    [Fact]
    public async Task The_schema_cache_is_cleared_by_the_migration_and_policy_rows_are_not()
    {
        // A cache row written before views existed describes a database that appears to have none. The
        // model would go on being told that until something unrelated invalidated it — so the migration
        // that introduces views is what has to drop it. Policy rows in the same store must survive
        // untouched: this migration changes no schema and owns no user decision.
        Guid connectionId, policyId;
        await using (var seed = NewContext())
        {
            await seed.GetService<IMigrator>().MigrateAsync("20260813161818_Projects");

            connectionId = Guid.NewGuid();
            policyId = Guid.NewGuid();
            seed.DatabaseConnections.Add(new DatabaseConnection
            {
                Id = connectionId,
                Name = "prod",
                ProviderType = DatabaseProviderType.Postgres,
                ConnectionStringSecretRef = "db:abc",
                IsReadOnly = false,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            seed.TablePolicies.Add(new TablePolicy
            {
                Id = policyId,
                DatabaseConnectionId = connectionId,
                SchemaName = "dbo",
                TableName = "secrets",
                IsVisible = false,
                CanRead = true,
                CanWrite = false,
            });
            seed.SchemaCaches.Add(new SchemaCache
            {
                Id = Guid.NewGuid(),
                DatabaseConnectionId = connectionId,
                SchemaHash = "stale",
                FilteredSchemaJson = """{"tables":[{"schema":"dbo","name":"orders","columns":[]}]}""",
                GeneratedAt = DateTime.UtcNow,
            });
            await seed.SaveChangesAsync();
        }
        SqliteConnection.ClearAllPools();

        await using var db = NewContext();
        await StoreInitializer.InitializeAsync(db, NullLogger.Instance);

        Assert.Empty(await db.SchemaCaches.ToListAsync());

        var policy = await db.TablePolicies.SingleAsync();
        Assert.Equal(policyId, policy.Id);
        Assert.False(policy.IsVisible);
        Assert.Equal("secrets", policy.TableName);
        Assert.Equal(connectionId, (await db.DatabaseConnections.SingleAsync()).Id);
    }
```

- [ ] **Step 2: Run it and watch it fail**

Run: `dotnet test tests/SqlAgent.Tests/SqlAgent.Tests.csproj --filter "FullyQualifiedName~The_schema_cache_is_cleared"`
Expected: FAIL — `Assert.Empty() Failure: Collection was not empty`.

- [ ] **Step 3: Generate the migration**

```bash
dotnet tool restore
dotnet ef migrations add ClearSchemaCacheForViews --project src/SqlAgent.Storage
```

This produces an empty `Up`/`Down` pair, because the model has not changed — that is expected, and it is the whole point: this migration carries data, not schema. The snapshot file will be regenerated identically; leave it as EF writes it.

- [ ] **Step 4: Write the data step**

Edit the generated `src/SqlAgent.Storage/Migrations/<timestamp>_ClearSchemaCacheForViews.cs` so the class reads:

```csharp
    /// <summary>
    /// A data migration, not a schema one. Phase C1 taught DatabaseSchema to carry views, which changed
    /// the shape of the JSON in SchemaCache. Every row written before that describes a database that
    /// appears to have no views, and SchemaService reuses a cached row without ever asking how old it is
    /// — so a view would silently never reach the model until some unrelated policy change invalidated
    /// the cache by accident.
    ///
    /// Deleting the rows is safe by construction: SchemaCache is a cache, GetOrRefreshAsync repopulates
    /// on the next read, and the only cost is one live extraction per connection.
    ///
    /// The general alternative — a format-version column that GetOrRefreshAsync checks, so a stale-format
    /// row is rejected rather than deleted — is deliberately not built. One recurrence is not a pattern,
    /// and it would trade a migration nobody has to think about for a check on the hot read path forever.
    /// </summary>
    public partial class ClearSchemaCacheForViews : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM \"SchemaCaches\";");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Nothing to undo. The rows this dropped were a cache, and going back one migration does not
            // make the old JSON correct again — it makes it correct to regenerate, which the next read does.
        }
    }
```

`SchemaCaches` is the table name EF derives from `SqlAgentDbContext.SchemaCaches`; there is no `ToTable` override to contradict it.

- [ ] **Step 5: Run the test and watch it pass**

Run: `dotnet test tests/SqlAgent.Tests/SqlAgent.Tests.csproj --filter "FullyQualifiedName~StoreMigrationTests"`
Expected: PASS, 11 tests.

- [ ] **Step 6: Run the full suite**

Run: `dotnet test tests/SqlAgent.Tests/SqlAgent.Tests.csproj`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add src/SqlAgent.Storage/Migrations tests/SqlAgent.Tests/StoreMigrationTests.cs
git commit -m "Clear the schema cache so views reach the model"
```

---

## Task 9: Two dimensions in TablePolicyService

**Files:**
- Modify: `src/SqlAgent.Storage/TablePolicyService.cs`
- Test: `tests/SqlAgent.Tests/TablePolicyServiceTests.cs` (create)

**Interfaces:**
- Consumes: `ObjectAccess` from Task 7, `DatabaseSchema.ViewList` from Task 3.
- Produces, used by Tasks 13 and 14:
  - `enum DatabaseObjectKind { Table, View }`
  - `record DatabaseObjectPolicy(string Schema, string Name, DatabaseObjectKind Kind, ObjectAccess Access)`
  - `enum SetAccessOutcome { Applied, ConnectionMissing, ViewCannotBeWritable }`
  - `Task<IReadOnlyList<DatabaseObjectPolicy>?> ListObjectsAsync(Guid connectionId, CancellationToken ct = default)`
  - `Task<SetAccessOutcome> SetAccessAsync(Guid connectionId, string schema, string name, DatabaseObjectKind kind, ObjectAccess access, CancellationToken ct = default)`
  - `Task<SetAccessOutcome> SetSchemaAccessAsync(Guid connectionId, string schema, ObjectAccess access, CancellationToken ct = default)`
- The old `TableVisibility` record and `ListAsync`/`SetVisibilityAsync` stay for now as thin wrappers, because `SchemaRail` and `SchemaRailTests` still call them. **Task 15 deletes all four together with the rail.** Do not build anything new on them.

- [ ] **Step 1: Write the failing tests**

Create `tests/SqlAgent.Tests/TablePolicyServiceTests.cs`:

```csharp
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
```

- [ ] **Step 2: Run them and watch them fail**

Run: `dotnet test tests/SqlAgent.Tests/SqlAgent.Tests.csproj --filter "FullyQualifiedName~TablePolicyServiceTests"`
Expected: FAIL to compile — `ListObjectsAsync`, `DatabaseObjectKind`, `SetAccessOutcome` do not exist.

- [ ] **Step 3: Write the service**

Replace `src/SqlAgent.Storage/TablePolicyService.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using SqlAgent.Core;
using SqlAgent.Core.Policy;

namespace SqlAgent.Storage;

/// <summary>Whether a live object is a base table or a view. Derived from the extracted schema on every
/// read — it is deliberately not a stored column, because nothing would read it back.</summary>
public enum DatabaseObjectKind
{
    Table,
    View,
}

/// <summary>One live object with its effective access level for a connection.</summary>
public record DatabaseObjectPolicy(
    string Schema, string Name, DatabaseObjectKind Kind, ObjectAccess Access);

/// <summary>Why a write did or did not happen. A bool cannot tell "no such connection" from "a view
/// cannot be writable", and the config page needs different words for each.</summary>
public enum SetAccessOutcome
{
    Applied,
    ConnectionMissing,
    ViewCannotBeWritable,
}

/// <summary>One live table with its effective visibility for a connection.</summary>
/// <remarks>Superseded by <see cref="DatabaseObjectPolicy"/>. It survives only because SchemaRail still
/// reads it, and both go away together when the rail does.</remarks>
public record TableVisibility(string Schema, string Table, bool IsVisible);

/// <summary>
/// Read/write side of per-object access (CD-50, extended in Phase C1). <see cref="SchemaService"/> only
/// ever exposes the already-filtered schema, so the config page needs this to see <em>every</em> live
/// object — hidden ones included — and set its level. An object with no <see cref="TablePolicy"/> row is
/// fully accessible; that is the rule every layer in this codebase applies, and it is why the store holds
/// rows only for objects somebody has actually decided about.
/// </summary>
public class TablePolicyService(
    DatabaseConnectionService connections, IDatabaseProviderRegistry providers, SqlAgentDbContext db)
{
    /// <summary>Every live table and view with its effective level, or null if the connection or its
    /// secret is missing. Ordered by schema then name, so the page can group without re-sorting.</summary>
    public async Task<IReadOnlyList<DatabaseObjectPolicy>?> ListObjectsAsync(
        Guid connectionId, CancellationToken ct = default)
    {
        var schema = await ExtractAsync(connectionId, ct);
        if (schema is null) return null;

        var byKey = await LoadPoliciesAsync(connectionId, ct);

        var tables = schema.Tables.Select(t => new DatabaseObjectPolicy(
            t.Schema, t.Name, DatabaseObjectKind.Table, AccessOf(byKey, t.Schema, t.Name)));
        var views = schema.ViewList.Select(v => new DatabaseObjectPolicy(
            v.Schema, v.Name, DatabaseObjectKind.View, AccessOf(byKey, v.Schema, v.Name)));

        return tables.Concat(views)
            .OrderBy(o => o.Schema, StringComparer.OrdinalIgnoreCase)
            .ThenBy(o => o.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Upserts one object's level, on every call — including <see cref="ObjectAccess.Full"/>, so a level
    /// the user chose is a row in the store rather than something inferred from a row's absence.
    /// <paramref name="kind"/> is an argument rather than a stored column: its only job is the view
    /// refusal below.
    /// </summary>
    public async Task<SetAccessOutcome> SetAccessAsync(
        Guid connectionId, string schema, string name,
        DatabaseObjectKind kind, ObjectAccess access, CancellationToken ct = default)
    {
        if (await connections.GetAsync(connectionId, ct) is null)
            return SetAccessOutcome.ConnectionMissing;

        if (kind == DatabaseObjectKind.View && access == ObjectAccess.Full)
            return SetAccessOutcome.ViewCannotBeWritable;

        await UpsertAsync(connectionId, schema, name, access, ct);
        await db.SaveChangesAsync(ct);
        await InvalidateAsync(connectionId, ct);
        return SetAccessOutcome.Applied;
    }

    /// <summary>
    /// Sets every object under one schema at once — the schema header row on the config page. A view in
    /// the batch is clamped to <see cref="ObjectAccess.ReadOnly"/> rather than failing the whole call:
    /// "everything here, as far as each object is allowed to go" is what the header means, and refusing
    /// the batch because one member is a view would make it useless on any schema that has one.
    /// </summary>
    public async Task<SetAccessOutcome> SetSchemaAccessAsync(
        Guid connectionId, string schema, ObjectAccess access, CancellationToken ct = default)
    {
        var objects = await ListObjectsAsync(connectionId, ct);
        if (objects is null) return SetAccessOutcome.ConnectionMissing;

        foreach (var o in objects.Where(o => string.Equals(o.Schema, schema, StringComparison.OrdinalIgnoreCase)))
        {
            var level = o.Kind == DatabaseObjectKind.View && access == ObjectAccess.Full
                ? ObjectAccess.ReadOnly
                : access;
            await UpsertAsync(connectionId, o.Schema, o.Name, level, ct);
        }

        await db.SaveChangesAsync(ct);
        await InvalidateAsync(connectionId, ct);
        return SetAccessOutcome.Applied;
    }

    /// <summary>Lists every live table with its effective visibility.</summary>
    /// <remarks>Superseded by <see cref="ListObjectsAsync"/>; alive only for SchemaRail, and removed with
    /// it. Do not build anything new on this.</remarks>
    public async Task<IReadOnlyList<TableVisibility>?> ListAsync(
        Guid connectionId, CancellationToken ct = default)
        => (await ListObjectsAsync(connectionId, ct))
            ?.Where(o => o.Kind == DatabaseObjectKind.Table)
            .Select(o => new TableVisibility(o.Schema, o.Name, o.Access != ObjectAccess.Hidden))
            .ToList();

    /// <summary>Upserts one table's visibility flag.</summary>
    /// <remarks>Superseded by <see cref="SetAccessAsync"/>; alive only for SchemaRail, and removed with
    /// it. Toggling on yields Full access, which is what the rail's checkbox has always meant.</remarks>
    public async Task<bool> SetVisibilityAsync(
        Guid connectionId, string schema, string table, bool isVisible, CancellationToken ct = default)
        => await SetAccessAsync(
            connectionId, schema, table, DatabaseObjectKind.Table,
            isVisible ? ObjectAccess.Full : ObjectAccess.Hidden, ct) == SetAccessOutcome.Applied;

    private async Task<DatabaseSchema?> ExtractAsync(Guid connectionId, CancellationToken ct)
    {
        var info = await connections.GetAsync(connectionId, ct);
        if (info is null) return null;
        var connectionString = await connections.ResolveConnectionStringAsync(connectionId, ct);
        if (connectionString is null) return null;
        return await providers.Get(info.ProviderType).GetSchemaAsync(connectionString, ct);
    }

    private async Task<Dictionary<(string, string), TablePolicy>> LoadPoliciesAsync(
        Guid connectionId, CancellationToken ct)
        => (await db.TablePolicies.Where(p => p.DatabaseConnectionId == connectionId).ToListAsync(ct))
            .ToDictionary(p => (p.SchemaName, p.TableName));

    /// <summary>
    /// The one place the three levels are read back out of the two columns. No row means full access;
    /// invisible outranks everything; visible-but-not-writable is read-only. CanRead is not consulted —
    /// no path writes it false, and treating it as an axis would invent a fourth state the UI cannot
    /// produce or display.
    /// </summary>
    private static ObjectAccess AccessOf(
        Dictionary<(string, string), TablePolicy> byKey, string schema, string name)
        => !byKey.TryGetValue((schema, name), out var p) ? ObjectAccess.Full
            : !p.IsVisible ? ObjectAccess.Hidden
            : p.CanWrite ? ObjectAccess.Full
            : ObjectAccess.ReadOnly;

    private async Task UpsertAsync(
        Guid connectionId, string schema, string name, ObjectAccess access, CancellationToken ct)
    {
        var policy = await db.TablePolicies.FirstOrDefaultAsync(
            p => p.DatabaseConnectionId == connectionId && p.SchemaName == schema && p.TableName == name, ct);
        if (policy is null)
        {
            policy = new TablePolicy
            {
                Id = Guid.NewGuid(),
                DatabaseConnectionId = connectionId,
                SchemaName = schema,
                TableName = name,
            };
            db.TablePolicies.Add(policy);
        }

        policy.IsVisible = access != ObjectAccess.Hidden;
        policy.CanRead = true;
        policy.CanWrite = access == ObjectAccess.Full;
    }

    /// <summary>Drops any cached schema so a now-hidden object cannot survive in a stale entry (CD-51
    /// Story 1.5). Called from every write path, not from the shared upsert, so a future caller that
    /// batches writes cannot accidentally skip it.</summary>
    private async Task InvalidateAsync(Guid connectionId, CancellationToken ct)
        => await db.SchemaCaches.Where(c => c.DatabaseConnectionId == connectionId).ExecuteDeleteAsync(ct);
}
```

- [ ] **Step 4: Run the tests and watch them pass**

Run: `dotnet test tests/SqlAgent.Tests/SqlAgent.Tests.csproj --filter "FullyQualifiedName~TablePolicyServiceTests"`
Expected: PASS, 17 test cases.

- [ ] **Step 5: Run the full suite**

Run: `dotnet test tests/SqlAgent.Tests/SqlAgent.Tests.csproj`
Expected: PASS. `SchemaRailTests` still passes through the wrappers — if it does not, the wrappers are not equivalent and that must be fixed here rather than deferred to Task 15.

- [ ] **Step 6: Commit**

```bash
git add src/SqlAgent.Storage/TablePolicyService.cs tests/SqlAgent.Tests/TablePolicyServiceTests.cs
git commit -m "Give TablePolicyService levels and views"
```

---

## Task 10: Enforce levels and view writes on the execution path

**Files:**
- Modify: `src/SqlAgent.Storage/QueryExecutionService.cs`
- Test: `tests/SqlAgent.Tests/QueryExecutionServiceTests.cs`

**Interfaces:**
- Consumes: `ObjectPolicy` / `ObjectAccess` (Task 7), `SchemaService.GetOrRefreshAsync` (existing), `DatabaseSchema.ViewList` (Task 3).
- Produces: `QueryExecutionService(DatabaseConnectionService, IDatabaseProviderRegistry, SqlAgentDbContext, SchemaService, ILogger<QueryExecutionService>, QueryExecutionOptions?)` — one new constructor parameter, before the logger. New failure code `schema_unavailable`.

**Note on a deliberate regression.** Identifying a view requires the schema, so every execution now reads
it — from `SchemaCache` when populated, live on first use. A connection that can run a query but cannot
read its own catalog will now be refused with `schema_unavailable` where it previously executed. That
connection is already unusable for `describe_schema` and the whole natural-language path, so the loss is
narrow; the alternative is a write to a view slipping through unnoticed, which is the failure this phase
exists to close.

- [ ] **Step 1: Write the failing tests**

In `tests/SqlAgent.Tests/QueryExecutionServiceTests.cs`, teach `ExecFakeProvider` to return a schema and
give `SetupAsync` the new dependency:

```csharp
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
```

The existing `GetSchemaAsync` threw `NotSupportedException`; every current test relied on the schema never
being read, which is exactly what changes here. Update `SetupAsync`:

```csharp
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
```

Then add the new cases:

```csharp
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
```

and the counting double, beside `ExecFakeProvider`:

```csharp
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
```

- [ ] **Step 2: Run them and watch them fail**

Run: `dotnet test tests/SqlAgent.Tests/SqlAgent.Tests.csproj --filter "FullyQualifiedName~QueryExecutionServiceTests"`
Expected: FAIL to compile — `QueryExecutionService` has no five-argument constructor taking a `SchemaService`.

- [ ] **Step 3: Take the dependency and widen the resolver**

In `src/SqlAgent.Storage/QueryExecutionService.cs`, add `SchemaService schemas` to the primary constructor,
between `db` and `logger`:

```csharp
public class QueryExecutionService(
    DatabaseConnectionService connections,
    IDatabaseProviderRegistry providers,
    SqlAgentDbContext db,
    SchemaService schemas,
    ILogger<QueryExecutionService> logger,
    QueryExecutionOptions? options = null)
```

Replace the resolver call site with one that can fail:

```csharp
        var resolve = await TryBuildPolicyResolverAsync(connectionId, ct);
        if (resolve is null)
        {
            const string msg = "The database schema could not be read, so this query could not be checked.";
            await AuditAsync(connectionId, sql, null, "deny", msg, null, null);
            return QueryExecutionResult.Failure(sql, "schema_unavailable", msg);
        }

        var decision = SqlPolicyValidator.Validate(sql, info.ProviderType, info.IsReadOnly, resolve);
```

and replace `BuildPolicyResolverAsync` with:

```csharp
    /// <summary>
    /// Builds the resolver the policy asks about every referenced object, or null when the schema could
    /// not be read.
    ///
    /// Two sources. Levels come from this connection's TablePolicy rows; an object with no row is fully
    /// accessible, which is the rule every layer here applies. Whether an object is a view comes from the
    /// schema, not from a policy row — given that default, a view nobody has touched has no row to read,
    /// so the policy table simply cannot answer the question. The cached copy is used when it exists, and
    /// that it is already visibility-filtered costs nothing: a hidden object is refused by the visibility
    /// branch before the view branch is reached.
    ///
    /// Matching is fail-closed for unqualified SQL. A bare name takes the most restrictive level among
    /// same-named objects across every schema, and counts as a view if any of them is one; a
    /// schema-qualified name must match the schema too. The consequence is worth knowing: a bare name
    /// matching a table in one schema and a view in another is treated as a view, and a write to it is
    /// refused. That is the same trade the hidden-object rule has always made.
    ///
    /// Returning null rather than throwing keeps ExecuteSqlAsync's contract — it answers with a result,
    /// never an exception — and denying is the fail-closed answer: without the schema a view cannot be
    /// told from a table, and allowing the query would let a write to a view through unchecked.
    /// </summary>
    private async Task<Func<SqlTableReference, ObjectPolicy>?> TryBuildPolicyResolverAsync(
        Guid connectionId, CancellationToken ct)
    {
        var rows = await db.TablePolicies
            .Where(p => p.DatabaseConnectionId == connectionId)
            .Select(p => new { p.SchemaName, p.TableName, p.IsVisible, p.CanWrite })
            .ToListAsync(ct);

        IReadOnlyList<SchemaView> views;
        try
        {
            var schema = await schemas.GetOrRefreshAsync(connectionId, ct);
            if (schema is null) return null;
            views = schema.ViewList;
        }
        catch (Exception ex)
        {
            // The provider's own text can echo a connection string, so it goes to the log and nowhere
            // else — the caller gets the fixed sentence at the call site above.
            logger.LogError(ex, "The schema for connection {ConnectionId} could not be read.", connectionId);
            return null;
        }

        return t =>
        {
            bool Matches(string schemaName, string objectName) =>
                string.Equals(objectName, t.Name, StringComparison.OrdinalIgnoreCase) &&
                (t.Schema is null || string.Equals(schemaName, t.Schema, StringComparison.OrdinalIgnoreCase));

            var matched = rows.Where(r => Matches(r.SchemaName, r.TableName)).ToList();

            // Most restrictive wins. No matching row at all means nobody has decided anything about this
            // object, which is full access.
            var access = matched.Count == 0
                ? ObjectAccess.Full
                : matched.Min(r => !r.IsVisible ? ObjectAccess.Hidden
                    : r.CanWrite ? ObjectAccess.Full
                    : ObjectAccess.ReadOnly);

            var isView = views.Any(v => Matches(v.Schema, v.Name));

            return new ObjectPolicy(access, isView);
        };
    }
```

`ObjectAccess` is declared least-to-most permissive, so `Min` is the fail-closed choice rather than a
coincidence — do not reorder the enum.

- [ ] **Step 4: Update the other construction sites**

`Program.cs` needs no change; `SchemaService` is already registered scoped and the container resolves the
new parameter. Search for any other direct construction:

Run: `grep -rn "new QueryExecutionService(" --include=*.cs --include=*.razor .`
Expected: only `tests/SqlAgent.Tests/QueryExecutionServiceTests.cs`, already updated in Step 1. Update any
other hit the same way.

- [ ] **Step 5: Run the tests and watch them pass**

Run: `dotnet test tests/SqlAgent.Tests/SqlAgent.Tests.csproj --filter "FullyQualifiedName~QueryExecutionServiceTests"`
Expected: PASS.

Existing tests in that file that assert a successful execution now read the schema through the fake, which
returns an empty `DatabaseSchema` by default — enough to satisfy the resolver. If one of them fails with
`schema_unavailable`, its provider double is still throwing from `GetSchemaAsync`; give it the new default.

- [ ] **Step 6: Run the full suite**

Run: `dotnet test tests/SqlAgent.Tests/SqlAgent.Tests.csproj`
Expected: PASS. `McpToolServiceTests`, `NlQueryServiceTests`, and `WorkspaceTests` all reach
`QueryExecutionService` through DI or their own stubs — any that fail with `schema_unavailable` need their
provider stub's `GetSchemaAsync` to return a schema rather than throw.

- [ ] **Step 7: Commit**

```bash
git add src/SqlAgent.Storage/QueryExecutionService.cs tests/SqlAgent.Tests/QueryExecutionServiceTests.cs
git commit -m "Enforce per-object levels and refuse writes to views"
```

---

## Task 11: Classify connection-test failures and keep driver text out of the browser

The parent spec forbids rendering provider exception text. `Connections.razor:184` renders it anyway,
behind a comment asserting the provider's `Error` is "a deliberately returned domain result, not exception
text" — which `PostgresProvider.cs:23` contradicts by passing `ex.Message` straight through. This task
makes the type system enforce what the comment claimed.

**Files:**
- Modify: `src/SqlAgent.Core/DatabaseProvider.cs:10-15`
- Create: `src/SqlAgent.Providers.Postgres/PostgresFailure.cs`
- Create: `src/SqlAgent.Providers.SqlServer/SqlServerFailure.cs`
- Modify: `src/SqlAgent.Providers.Postgres/PostgresProvider.cs:21-24`
- Modify: `src/SqlAgent.Providers.SqlServer/SqlServerProvider.cs:21-24`
- Modify: `src/SqlAgent.Storage/ConnectionTester.cs`
- Create: `src/SqlAgent.Host/Web/ConnectionFailureText.cs`
- Modify: `src/SqlAgent.Host/Components/Pages/Connections.razor:163-185` (temporary; the file is deleted in Task 15)
- Test: `tests/SqlAgent.Tests/ConnectionFailureTests.cs` (create), `tests/SqlAgent.Tests/ProviderTests.cs`, `tests/SqlAgent.Tests/ProviderIntegrationTests.cs:23`

**Interfaces:**
- Produces, used by Task 13:
  - `enum ConnectionFailure { None, AuthenticationFailed, DatabaseNotFound, HostUnreachable, Timeout, Unknown }`
  - `record ConnectionTestResult(bool Success, ConnectionFailure Failure, string? Diagnostic, string? ServerVersion = null, long ElapsedMs = 0)` with `Ok(string?, long)` unchanged in signature and `Fail(ConnectionFailure, string, long)`
  - `record ConnectionTestOutcome(bool Success, ConnectionFailure Failure, string? ServerVersion, long ElapsedMs)`
  - `ConnectionTester.TestDraftAsync(DatabaseProviderType, string, CancellationToken)` → `Task<ConnectionTestOutcome>`
  - `ConnectionTester.TestSavedAsync(Guid, CancellationToken)` → `Task<ConnectionTestOutcome?>`
  - `ConnectionFailureText.Code(ConnectionFailure)` and `ConnectionFailureText.Message(ConnectionFailure)`

- [ ] **Step 1: Write the failing tests**

Create `tests/SqlAgent.Tests/ConnectionFailureTests.cs`:

```csharp
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
```

`RecordingLoggerProvider.CreateLogger` returns a plain `ILogger`, which is why the one-line `TypedLogger`
adapter is here rather than a cast — `ILogger` is not `ILogger<T>`, and there is no built-in adapter that
does not also drag in a whole logging pipeline.

- [ ] **Step 2: Run them and watch them fail**

Run: `dotnet test tests/SqlAgent.Tests/SqlAgent.Tests.csproj --filter "FullyQualifiedName~ConnectionFailureTests"`
Expected: FAIL to compile — `ConnectionFailure`, `PostgresFailure`, `SqlServerFailure`, `ConnectionFailureText` do not exist.

- [ ] **Step 3: Reshape the result in Core**

In `src/SqlAgent.Core/DatabaseProvider.cs`, replace the `ConnectionTestResult` record:

```csharp
/// <summary>
/// Why a connection test failed, in terms the UI has words for. A closed set on purpose: the driver's own
/// sentence can name the host, the database, the user, and — depending on the driver and the failure —
/// echo connection-string keywords back, so it is not something to render.
/// </summary>
public enum ConnectionFailure
{
    None = 0,
    AuthenticationFailed,
    DatabaseNotFound,
    HostUnreachable,
    Timeout,
    Unknown,
}

/// <summary>
/// Outcome of a connection test at the provider level. Never throws back to the caller for a
/// reachable-but-rejecting server. <see cref="Diagnostic"/> is the driver's own text and is for the log
/// only — <see cref="ConnectionTestOutcome"/> is what leaves Storage, and it has no field to carry it.
/// </summary>
public record ConnectionTestResult(
    bool Success,
    ConnectionFailure Failure,
    string? Diagnostic,
    string? ServerVersion = null,
    long ElapsedMs = 0)
{
    public static ConnectionTestResult Ok(string? serverVersion, long elapsedMs)
        => new(true, ConnectionFailure.None, null, serverVersion, elapsedMs);

    public static ConnectionTestResult Fail(ConnectionFailure failure, string diagnostic, long elapsedMs)
        => new(false, failure, diagnostic, null, elapsedMs);
}

/// <summary>
/// What a connection test looks like above Storage. Deliberately a different type from
/// <see cref="ConnectionTestResult"/> rather than the same one with a rule attached: the driver text is
/// absent because there is nowhere to put it, which is a guarantee a comment cannot make.
/// </summary>
public record ConnectionTestOutcome(
    bool Success, ConnectionFailure Failure, string? ServerVersion, long ElapsedMs);
```

- [ ] **Step 4: Write the two classifiers**

Create `src/SqlAgent.Providers.Postgres/PostgresFailure.cs`:

```csharp
using SqlAgent.Core;

namespace SqlAgent.Providers.Postgres;

/// <summary>
/// Maps what Npgsql reports about a failed connection onto <see cref="ConnectionFailure"/>. A pure
/// function over the pieces the provider pulled out of the exception, rather than a method over the
/// exception itself, so it can be unit-tested — the SQL Server twin has no choice, since SqlException
/// cannot be constructed outside its own assembly, and the two are kept symmetrical on purpose.
/// </summary>
public static class PostgresFailure
{
    public static ConnectionFailure Classify(string? sqlState, bool isSocketFailure, bool isTimeout)
    {
        // Timeout first: a request that timed out often also reports a socket error on the way out, and
        // "unreachable" would send the user to check an address that is probably right.
        if (isTimeout) return ConnectionFailure.Timeout;

        return sqlState switch
        {
            // 28P01 invalid_password, 28000 invalid_authorization_specification.
            "28P01" or "28000" => ConnectionFailure.AuthenticationFailed,
            // 3D000 invalid_catalog_name — the server is there, the database is not.
            "3D000" => ConnectionFailure.DatabaseNotFound,
            // A SQLSTATE at all means the server answered, so it was reachable whatever else went wrong.
            not null => ConnectionFailure.Unknown,
            _ => isSocketFailure ? ConnectionFailure.HostUnreachable : ConnectionFailure.Unknown,
        };
    }
}
```

Create `src/SqlAgent.Providers.SqlServer/SqlServerFailure.cs`:

```csharp
using SqlAgent.Core;

namespace SqlAgent.Providers.SqlServer;

/// <summary>
/// Maps what SqlClient reports about a failed connection onto <see cref="ConnectionFailure"/>. Pure by
/// necessity: SqlException has no public constructor, so a classifier taking the exception could never be
/// exercised by a unit test, only by a live server.
/// </summary>
public static class SqlServerFailure
{
    public static ConnectionFailure Classify(int? number, bool isSocketFailure, bool isTimeout)
    {
        if (isTimeout || number == -2) return ConnectionFailure.Timeout;

        return number switch
        {
            // 18456 login failed, 18452 login from an untrusted domain.
            18456 or 18452 => ConnectionFailure.AuthenticationFailed,
            // 4060 cannot open database, 911 database does not exist.
            4060 or 911 => ConnectionFailure.DatabaseNotFound,
            // 2 server not found, 53 network path not found, 10060/10061 refused or timed out at the
            // socket, 11001 host not found, 40615 Azure firewall.
            2 or 53 or 10060 or 10061 or 11001 or 40615 => ConnectionFailure.HostUnreachable,
            null => isSocketFailure ? ConnectionFailure.HostUnreachable : ConnectionFailure.Unknown,
            _ => ConnectionFailure.Unknown,
        };
    }
}
```

- [ ] **Step 5: Classify in both providers**

In `src/SqlAgent.Providers.Postgres/PostgresProvider.cs`, replace the catch in `TestConnectionAsync`:

```csharp
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Everything the classifier needs, pulled out here so the mapping itself stays testable.
            var sqlState = ex is NpgsqlException { SqlState: { } state } ? state : null;
            var socket = ex.GetBaseException() is System.Net.Sockets.SocketException;
            var timeout = ex.GetBaseException() is TimeoutException;

            return ConnectionTestResult.Fail(
                PostgresFailure.Classify(sqlState, socket, timeout), ex.Message, sw.ElapsedMilliseconds);
        }
```

If this Npgsql version does not expose `SqlState` on `NpgsqlException`, read it from `PostgresException`
instead (`ex is PostgresException pg ? pg.SqlState : null`) — check which type carries it before writing
the line rather than after the compiler complains.

In `src/SqlAgent.Providers.SqlServer/SqlServerProvider.cs`:

```csharp
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var number = ex is SqlException sql ? sql.Number : (int?)null;
            var socket = ex.GetBaseException() is System.Net.Sockets.SocketException;
            var timeout = ex.GetBaseException() is TimeoutException;

            return ConnectionTestResult.Fail(
                SqlServerFailure.Classify(number, socket, timeout), ex.Message, sw.ElapsedMilliseconds);
        }
```

- [ ] **Step 6: Make ConnectionTester the boundary**

Replace `src/SqlAgent.Storage/ConnectionTester.cs`:

```csharp
using Microsoft.Extensions.Logging;
using SqlAgent.Core;

namespace SqlAgent.Storage;

/// <summary>
/// Tests database connections by selecting the provider from the stored/declared
/// <see cref="DatabaseProviderType"/>. Draft = caller-supplied string; saved = resolved from the secret
/// store.
///
/// This class is the boundary the driver's own text does not cross. It takes a
/// <see cref="ConnectionTestResult"/>, writes its <c>Diagnostic</c> to the log, and returns a
/// <see cref="ConnectionTestOutcome"/> — a type with nowhere to put the text. The parent spec has said
/// since phase A that provider exception text is never rendered; before this it was a rule enforced by a
/// comment, and the comment turned out to describe something the code did not do.
/// </summary>
public class ConnectionTester(
    DatabaseConnectionService connections,
    IDatabaseProviderRegistry providers,
    ILogger<ConnectionTester> logger)
{
    /// <summary>Tests an unsaved connection string against the given provider type.</summary>
    public async Task<ConnectionTestOutcome> TestDraftAsync(
        DatabaseProviderType providerType, string connectionString, CancellationToken ct = default)
        => Cross(await providers.Get(providerType).TestConnectionAsync(connectionString, ct), null);

    /// <summary>Tests a saved connection by id: resolves its secret and uses its stored provider type.
    /// Null when the connection or its secret is missing.</summary>
    public async Task<ConnectionTestOutcome?> TestSavedAsync(Guid id, CancellationToken ct = default)
    {
        var info = await connections.GetAsync(id, ct);
        if (info is null) return null;
        var connectionString = await connections.ResolveConnectionStringAsync(id, ct);
        if (connectionString is null) return null;
        return Cross(await providers.Get(info.ProviderType).TestConnectionAsync(connectionString, ct), id);
    }

    private ConnectionTestOutcome Cross(ConnectionTestResult result, Guid? connectionId)
    {
        if (!result.Success && !string.IsNullOrEmpty(result.Diagnostic))
        {
            // Warning, not Error: a rejecting server is an ordinary outcome for a tool pointed at
            // arbitrary databases, but it is the one thing anyone will want out of the log afterwards.
            logger.LogWarning(
                "Connection test failed for {Connection} ({Failure}). The driver reported: {Diagnostic}",
                connectionId?.ToString() ?? "a draft connection", result.Failure, result.Diagnostic);
        }

        return new ConnectionTestOutcome(
            result.Success, result.Failure, result.ServerVersion, result.ElapsedMs);
    }
}
```

- [ ] **Step 7: Give the Host words for each outcome**

Create `src/SqlAgent.Host/Web/ConnectionFailureText.cs`:

```csharp
using SqlAgent.Core;

namespace SqlAgent.Host.Web;

/// <summary>
/// The stable code and the sentence each connection-test failure renders with. One place, because two
/// surfaces show these and a page that invents its own wording is how a code and its message drift apart.
/// Every sentence is written here rather than derived from anything the driver said.
/// </summary>
public static class ConnectionFailureText
{
    public static string Code(ConnectionFailure failure) => failure switch
    {
        ConnectionFailure.AuthenticationFailed => "connection_test_auth_failed",
        ConnectionFailure.DatabaseNotFound => "connection_test_database_not_found",
        ConnectionFailure.HostUnreachable => "connection_test_host_unreachable",
        ConnectionFailure.Timeout => "connection_test_timeout",
        _ => "connection_test_failed",
    };

    public static string Message(ConnectionFailure failure) => failure switch
    {
        ConnectionFailure.AuthenticationFailed =>
            "The server rejected the credentials in the connection string.",
        ConnectionFailure.DatabaseNotFound =>
            "The server answered, but it has no database by that name.",
        ConnectionFailure.HostUnreachable =>
            "The server could not be reached at that address.",
        ConnectionFailure.Timeout =>
            "The server did not answer before the connection timed out.",
        // Deliberately points at the log rather than saying nothing: the detail exists, it is simply not
        // safe to put in a browser.
        _ => "The connection failed. The details are in the server log.",
    };
}
```

- [ ] **Step 8: Update the existing callers**

`src/SqlAgent.Host/Components/Pages/Connections.razor` — this file is deleted in Task 15, so change only
what the compiler demands. In `TestAsync`, replace the result type and the three branches:

```csharp
        ConnectionTestOutcome? result;
        try
        {
            result = await Runner.RunAsync<ConnectionTester, ConnectionTestOutcome?>(t => t.TestSavedAsync(id));
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to test connection {ConnectionId}.", id);
            Fail("The connection could not be tested. The details are in the server log.");
            return;
        }
        if (result is null)
            Refuse("connection_secret_missing", "Connection or its secret is missing.");
        else if (result.Success)
            Succeed($"Connection OK ({result.ServerVersion}, {result.ElapsedMs} ms)");
        else
            Refuse(ConnectionFailureText.Code(result.Failure), ConnectionFailureText.Message(result.Failure));
```

Delete the stale comment above those branches — it justified rendering `result.Error`, which no longer
exists.

`tests/SqlAgent.Tests/ProviderIntegrationTests.cs:23` — `Assert.True(connection.Success, connection.Error)`
becomes `Assert.True(connection.Success, connection.Failure.ToString())`.

`tests/SqlAgent.Tests/ProviderTests.cs` — `ConnectionTestResult.Fail("wrong provider", 0)` becomes
`ConnectionTestResult.Fail(ConnectionFailure.Unknown, "wrong provider", 0)`, and every `new ConnectionTester(...)`
gains a third argument (`NullLogger<ConnectionTester>.Instance`). Its `TestSavedAsync`/`TestDraftAsync`
assertions now read `ConnectionTestOutcome`.

Run this to find every remaining site:

Run: `grep -rn "ConnectionTestResult\|ConnectionTester(" --include=*.cs --include=*.razor src tests`

- [ ] **Step 9: Run the tests and watch them pass**

Run: `dotnet test tests/SqlAgent.Tests/SqlAgent.Tests.csproj --filter "FullyQualifiedName~ConnectionFailureTests"`
Expected: PASS.

- [ ] **Step 10: Run the full suite**

Run: `dotnet test tests/SqlAgent.Tests/SqlAgent.Tests.csproj`
Expected: PASS. `ConnectionsPageTests` asserts on the old rendered driver text — update its expectation to
the classified sentence rather than deleting the test; Task 15 removes the file, and until then it is what
proves the page still works.

- [ ] **Step 11: Commit**

```bash
git add src/SqlAgent.Core/DatabaseProvider.cs src/SqlAgent.Providers.Postgres src/SqlAgent.Providers.SqlServer src/SqlAgent.Storage/ConnectionTester.cs src/SqlAgent.Host/Web/ConnectionFailureText.cs src/SqlAgent.Host/Components/Pages/Connections.razor tests/SqlAgent.Tests
git commit -m "Classify connection-test failures and keep driver text out of the browser"
```

---

## Task 12: The Databases section in the sidebar

**Files:**
- Modify: `src/SqlAgent.Host/Web/AppState.cs`
- Create: `src/SqlAgent.Host/Components/Layout/DatabaseSection.razor`
- Create: `src/SqlAgent.Host/Components/Layout/DatabaseSection.razor.css`
- Modify: `src/SqlAgent.Host/Components/Layout/Sidebar.razor:49-56`
- Modify: `src/SqlAgent.Host/Components/Layout/SidebarNav.razor:20-23`
- Test: `tests/SqlAgent.Tests/DatabaseSectionTests.cs` (create)

**Interfaces:**
- Produces, used by Task 13:
  - `enum ConnectionStatus { Untested, Ok, Failed }` in `SqlAgent.Host.Web`
  - `AppState.StatusOf(Guid) → ConnectionStatus`
  - `AppState.RecordTest(Guid, bool success)`
  - `AppState.ForgetStatus(Guid)`
  - `event Action? AppState.ConnectionStatusChanged`
- Routes: the section links each row to `/database/{id}` and its add button to `/database`. Task 13 creates that page; until then the links 404, which is expected between these two tasks.

- [ ] **Step 1: Write the failing tests**

Create `tests/SqlAgent.Tests/DatabaseSectionTests.cs`:

```csharp
using Bunit;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SqlAgent.Core;
using SqlAgent.Host.Components.Layout;
using SqlAgent.Host.Web;
using SqlAgent.Storage;

namespace SqlAgent.Tests;

public class DatabaseSectionTests : IDisposable
{
    private readonly SqliteConnection _conn = new("DataSource=:memory:");
    private readonly Bunit.TestContext _ctx = new();

    public DatabaseSectionTests()
    {
        _conn.Open();
        _ctx.Services.AddDbContext<SqlAgentDbContext>(o => o.UseSqlite(_conn));
        _ctx.Services.AddScoped<DatabaseConnectionService>();
        _ctx.Services.AddScoped<ISecretStore, InMemorySecretStore>();
        _ctx.Services.AddScoped<ScopedRunner>();
        _ctx.Services.AddScoped<AppState>();
        // The section injects ILogger<DatabaseSection>. AddLogging registers the open generic, so every
        // component in these tests gets one without naming them individually.
        _ctx.Services.AddLogging();
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        using var scope = _ctx.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<SqlAgentDbContext>().Database.EnsureCreated();
    }

    public void Dispose() { _ctx.Dispose(); _conn.Dispose(); }

    private async Task<Guid> SeedAsync(string name, DatabaseProviderType type = DatabaseProviderType.Postgres)
    {
        using var scope = _ctx.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<DatabaseConnectionService>();
        return (await svc.CreateAsync(new DatabaseConnectionInput(name, type, true), "cs")).Id;
    }

    [Fact]
    public void With_no_databases_only_the_heading_and_its_add_button_render()
    {
        // Same judgement the project section made: "no databases yet" tells the user nothing the empty
        // list does not already say, and the add button is right there.
        var section = _ctx.RenderComponent<DatabaseSection>();

        Assert.Empty(section.FindAll(".database-row"));
        Assert.Single(section.FindAll("[data-testid=database-add]"));
    }

    [Fact]
    public async Task A_database_shows_its_name_and_its_engine()
    {
        await SeedAsync("warehouse", DatabaseProviderType.SqlServer);

        var section = _ctx.RenderComponent<DatabaseSection>();

        var row = Assert.Single(section.FindAll(".database-row"));
        Assert.Contains("warehouse", row.TextContent);
        Assert.Contains("SQL Server", row.TextContent);
    }

    [Fact]
    public async Task A_row_links_to_that_database_and_the_add_button_to_a_new_one()
    {
        var id = await SeedAsync("warehouse");

        var section = _ctx.RenderComponent<DatabaseSection>();

        Assert.Equal($"/database/{id}", section.Find(".database-open").GetAttribute("href"));
        Assert.Equal("/database", section.Find("[data-testid=database-add]").GetAttribute("href"));
    }

    [Fact]
    public async Task A_database_is_untested_until_something_records_a_test()
    {
        var id = await SeedAsync("warehouse");
        var section = _ctx.RenderComponent<DatabaseSection>();

        Assert.Contains("untested", section.Find(".database-dot").GetAttribute("class"));

        var state = _ctx.Services.GetRequiredService<AppState>();
        await section.InvokeAsync(() => state.RecordTest(id, success: true));

        Assert.Contains("ok", section.Find(".database-dot").GetAttribute("class"));
    }

    [Fact]
    public async Task A_failed_test_shows_a_failed_dot()
    {
        var id = await SeedAsync("warehouse");
        var section = _ctx.RenderComponent<DatabaseSection>();
        var state = _ctx.Services.GetRequiredService<AppState>();

        await section.InvokeAsync(() => state.RecordTest(id, success: false));

        Assert.Contains("failed", section.Find(".database-dot").GetAttribute("class"));
    }

    [Fact]
    public async Task The_dot_state_is_announced_not_only_coloured()
    {
        // A coloured dot with no text is invisible to a screen reader and to anyone who cannot tell the
        // two colours apart.
        var id = await SeedAsync("warehouse");
        var section = _ctx.RenderComponent<DatabaseSection>();
        var state = _ctx.Services.GetRequiredService<AppState>();

        await section.InvokeAsync(() => state.RecordTest(id, success: false));

        Assert.Contains("failed", section.Find(".database-dot .sr-only").TextContent,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_list_re_reads_itself_when_the_connection_set_changes()
    {
        // The section is a sibling of every page, not a child, and MainLayout is not recreated across
        // navigation — the same reason HistorySection and the old rail both subscribe.
        var section = _ctx.RenderComponent<DatabaseSection>();
        Assert.Empty(section.FindAll(".database-row"));

        await SeedAsync("warehouse");
        var state = _ctx.Services.GetRequiredService<AppState>();
        await section.InvokeAsync(state.NotifyConnectionsChanged);

        Assert.Single(section.FindAll(".database-row"));
    }

    [Fact]
    public async Task The_list_collapses_and_expands()
    {
        await SeedAsync("warehouse");
        var section = _ctx.RenderComponent<DatabaseSection>();

        Assert.Single(section.FindAll(".database-row"));

        await section.Find("[data-testid=databases-toggle]").ClickAsync(new MouseEventArgs());
        Assert.Empty(section.FindAll(".database-row"));

        await section.Find("[data-testid=databases-toggle]").ClickAsync(new MouseEventArgs());
        Assert.Single(section.FindAll(".database-row"));
    }
}
```

Add to `tests/SqlAgent.Tests/AppStateTests.cs`:

```csharp
    [Fact]
    public void Connection_status_starts_untested_and_records_both_outcomes()
    {
        var state = new AppState();
        var id = Guid.NewGuid();

        Assert.Equal(ConnectionStatus.Untested, state.StatusOf(id));

        state.RecordTest(id, success: true);
        Assert.Equal(ConnectionStatus.Ok, state.StatusOf(id));

        state.RecordTest(id, success: false);
        Assert.Equal(ConnectionStatus.Failed, state.StatusOf(id));
    }

    [Fact]
    public void Recording_the_same_status_twice_raises_nothing()
    {
        // The section re-reads its whole list on this event. Firing it for an unchanged value would
        // re-query SQLite every time a user pressed Test on an already-passing connection.
        var state = new AppState();
        var id = Guid.NewGuid();
        var raised = 0;
        state.ConnectionStatusChanged += () => raised++;

        state.RecordTest(id, success: true);
        state.RecordTest(id, success: true);

        Assert.Equal(1, raised);
    }

    [Fact]
    public void Forgetting_a_status_raises_only_when_there_was_one()
    {
        var state = new AppState();
        var id = Guid.NewGuid();
        var raised = 0;
        state.ConnectionStatusChanged += () => raised++;

        state.ForgetStatus(id);
        Assert.Equal(0, raised);

        state.RecordTest(id, success: true);
        state.ForgetStatus(id);
        Assert.Equal(2, raised);
        Assert.Equal(ConnectionStatus.Untested, state.StatusOf(id));
    }
```

- [ ] **Step 2: Run them and watch them fail**

Run: `dotnet test tests/SqlAgent.Tests/SqlAgent.Tests.csproj --filter "FullyQualifiedName~DatabaseSectionTests|FullyQualifiedName~AppStateTests"`
Expected: FAIL to compile — `DatabaseSection` and `ConnectionStatus` do not exist.

- [ ] **Step 3: Add the status to AppState**

In `src/SqlAgent.Host/Web/AppState.cs`, above the class:

```csharp
/// <summary>How the last connection test in this session went. There is no background poller, so
/// "untested" is the honest answer until somebody presses the button.</summary>
public enum ConnectionStatus
{
    Untested,
    Ok,
    Failed,
}
```

and inside it, beside the other connection members:

```csharp
    private readonly Dictionary<Guid, ConnectionStatus> _connectionStatus = [];

    /// <summary>
    /// The dot beside a database in the sidebar. Scoped to the circuit like everything else here, which
    /// is deliberate: a status persisted across restarts would claim a database was reachable at a moment
    /// nobody checked, and a background poller against arbitrary remote servers is a feature nobody asked
    /// for. Unknown until this session tested it.
    /// </summary>
    public ConnectionStatus StatusOf(Guid connectionId)
        => _connectionStatus.GetValueOrDefault(connectionId, ConnectionStatus.Untested);

    /// <summary>Raised when a dot should change. Separate from <see cref="ConnectionsChanged"/> because
    /// the set of databases has not moved — only what is known about one of them.</summary>
    public event Action? ConnectionStatusChanged;

    /// <summary>Records the result of a test. Silent on an unchanged value: the section re-reads its whole
    /// list from SQLite on this event, and pressing Test twice on a working connection should not cost
    /// two queries and two renders.</summary>
    public void RecordTest(Guid connectionId, bool success)
    {
        var next = success ? ConnectionStatus.Ok : ConnectionStatus.Failed;
        if (_connectionStatus.TryGetValue(connectionId, out var current) && current == next) return;
        _connectionStatus[connectionId] = next;
        ConnectionStatusChanged?.Invoke();
    }

    /// <summary>Drops what was known about a connection — called when one is deleted, so a later
    /// connection reusing the id (or a stale render) cannot inherit its dot.</summary>
    public void ForgetStatus(Guid connectionId)
    {
        if (_connectionStatus.Remove(connectionId))
            ConnectionStatusChanged?.Invoke();
    }
```

- [ ] **Step 4: Write the section**

Create `src/SqlAgent.Host/Components/Layout/DatabaseSection.razor`:

```razor
@implements IDisposable
@inject ScopedRunner Runner
@inject AppState State
@inject ILogger<DatabaseSection> Logger

<div class="databases">
    <div class="databases-head">
        <button type="button" class="ghost databases-toggle" data-testid="databases-toggle"
                aria-expanded="@_expanded" @onclick="() => _expanded = !_expanded">
            @* Two elements under an @if rather than one with a computed Name: the glyph inventory test
               reads this markup with a regex, and a name built inside an attribute expression is not
               something it can read honestly. Same shape as ProjectSection's chevron. *@
            @if (_expanded)
            {
                <Icon Name="chevron-down" Size="14" />
            }
            else
            {
                <Icon Name="chevron-right" Size="14" />
            }
            <span class="databases-label">Databases</span>
        </button>
        @* A plain anchor, not a NavLink: NavLink would mark it active for every /database/{id} route
           under its default prefix match, so the add button would look selected while you edit an
           existing database. *@
        <a class="ghost databases-add" href="/database" data-testid="database-add" aria-label="Add database">
            <Icon Name="plus" Size="16" />
        </a>
    </div>

    @if (_error is not null)
    {
        @* No code: this is "the app broke", not a deliberate refusal, so it must not look like one. *@
        <OutcomeMessage Message="@_error" />
    }

    @if (_expanded)
    {
        @foreach (var c in _connections)
        {
            <div class="database-row" @key="c.Id">
                <NavLink class="database-open truncate" href="@($"/database/{c.Id}")">
                    <Icon Name="database" Size="16" />
                    <span class="truncate">@c.Name</span>
                    <Badge Tone="BadgeTone.Neutral">@EngineLabel(c.ProviderType)</Badge>
                    <span class="database-dot @DotClass(c.Id)" aria-hidden="true"></span>
                    <span class="sr-only">@DotLabel(c.Id)</span>
                </NavLink>
            </div>
        }
    }
</div>

@code {
    private IReadOnlyList<DatabaseConnectionInfo> _connections = [];
    private bool _expanded = true;
    private string? _error;

    protected override async Task OnInitializedAsync()
    {
        // Every page is a sibling of this section, not a child, and MainLayout is not recreated across
        // navigation — so nothing re-runs this method when the config page saves or deletes something.
        // HistorySection carries the same pair of subscriptions for the same reason.
        State.ConnectionsChanged += OnConnectionsChanged;
        State.ConnectionStatusChanged += OnStatusChanged;
        await ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        try
        {
            _connections = await Runner.RunAsync<DatabaseConnectionService, IReadOnlyList<DatabaseConnectionInfo>>(
                s => s.ListAsync());
            _error = null;
        }
        catch (Exception ex)
        {
            // A locked or missing SQLite file is a real possibility, and this section sits outside
            // WorkArea's ErrorBoundary — letting it escape takes the circuit down. The exception text is
            // never rendered: it can carry a file path.
            Logger.LogError(ex, "Failed to list saved connections for the sidebar.");
            _connections = [];
            _error = "The saved databases could not be read. The details are in the server log.";
        }
    }

    private static string EngineLabel(DatabaseProviderType type) => type switch
    {
        DatabaseProviderType.SqlServer => "SQL Server",
        DatabaseProviderType.Postgres => "Postgres",
        _ => type.ToString(),
    };

    private string DotClass(Guid id) => State.StatusOf(id) switch
    {
        ConnectionStatus.Ok => "ok",
        ConnectionStatus.Failed => "failed",
        _ => "untested",
    };

    // Rendered as text beside the dot rather than as a title on it: a colour is not a status to anyone
    // using a screen reader, and a tooltip is not reachable from the keyboard.
    private string DotLabel(Guid id) => State.StatusOf(id) switch
    {
        ConnectionStatus.Ok => "Last test in this session: connected",
        ConnectionStatus.Failed => "Last test in this session: failed",
        _ => "Not tested in this session",
    };

    private void OnConnectionsChanged() => InvokeAsync(async () =>
    {
        await ReloadAsync();
        StateHasChanged();
    });

    // No re-read: nothing about the stored rows changed, only what this session knows about one of them.
    private void OnStatusChanged() => InvokeAsync(StateHasChanged);

    public void Dispose()
    {
        State.ConnectionsChanged -= OnConnectionsChanged;
        State.ConnectionStatusChanged -= OnStatusChanged;
    }
}
```

Create `src/SqlAgent.Host/Components/Layout/DatabaseSection.razor.css`, in the same idiom as
`ProjectSection.razor.css`:

```css
.databases { display: flex; flex-direction: column; gap: 2px; margin-bottom: var(--space-4); }

.databases-head { display: flex; align-items: center; justify-content: space-between; }
.databases-toggle { display: flex; align-items: center; gap: var(--space-2); padding: 0 var(--space-2); }
.databases-label { color: var(--text-100); font-size: var(--text-xs); font-weight: 500; }
.databases-add { padding: var(--space-1); line-height: 0; }

.database-row { display: flex; align-items: center; border-radius: var(--radius-control); }
.database-row:hover { background: var(--background-soft-100); }

.database-open {
  flex: 1;
  min-width: 0;
  display: flex;
  align-items: center;
  gap: var(--space-2);
  text-align: left;
  padding: var(--space-2);
  color: var(--text-50);
}

/* The dot is decorative; the state is announced by the sr-only span beside it. Sized in em so it tracks
   the row's text rather than needing its own breakpoint. */
.database-dot { width: .5em; height: .5em; border-radius: 50%; margin-left: auto; flex: none; }
.database-dot.untested { background: var(--text-300); }
.database-dot.ok { background: var(--success); }
.database-dot.failed { background: var(--danger); }
```

Check the three colour tokens against `app.css` before writing them — this file must not invent a token.
If `--success` / `--danger` are named differently there, use the names that exist.

- [ ] **Step 5: Put it in the sidebar and retarget the nav row**

In `src/SqlAgent.Host/Components/Layout/Sidebar.razor`, the body becomes:

```razor
    <div class="sidebar-body custom-scroll">
        <DatabaseSection />
        <ProjectSection />
        <HistorySection />

        @* Phase C1 replaces the rail with the section above and the config page. Task 15 deletes it; until
           then it is still the only place a connection can be picked. *@
        <SchemaRail />
    </div>
```

In `src/SqlAgent.Host/Components/Layout/SidebarNav.razor`, replace the Connections row:

```razor
    <NavLink class="nav-row" href="/database">
        <Icon Name="database" Size="18" />
        <span class="nav-label">Databases</span>
    </NavLink>
```

The row survives even though the section duplicates it, unlike projects and history which have none:
`sidebar-body` is hidden entirely when the sidebar is collapsed to the icon rail, and `theme.js` restores
a collapsed sidebar before first paint — so without this row a fresh install opening collapsed has no path
to "add a database" at all. Add that sentence as a comment above the row.

- [ ] **Step 6: Run the tests and watch them pass**

Run: `dotnet test tests/SqlAgent.Tests/SqlAgent.Tests.csproj --filter "FullyQualifiedName~DatabaseSectionTests|FullyQualifiedName~AppStateTests"`
Expected: PASS.

- [ ] **Step 7: Run the full suite**

Run: `dotnet test tests/SqlAgent.Tests/SqlAgent.Tests.csproj`
Expected: PASS. `ShellTests` and `SidebarCollapseParityTests` assert on the sidebar's contents — a failure
there is a real expectation to update (the nav row's label changed from "Connections" to "Databases"), not
a test to delete.

- [ ] **Step 8: Commit**

```bash
git add src/SqlAgent.Host/Web/AppState.cs src/SqlAgent.Host/Components/Layout tests/SqlAgent.Tests
git commit -m "Add the Databases section to the sidebar"
```

---

## Task 13: The config page and its connection panel

**Files:**
- Create: `src/SqlAgent.Host/Components/Pages/DatabasePage.razor`
- Modify: `src/SqlAgent.Host/wwwroot/css/app.css` (the shared panel/field/actions rules)
- Create: `src/SqlAgent.Host/Components/Shared/Database/ConnectionPanel.razor`
- Create: `src/SqlAgent.Host/Components/Shared/Database/ConnectionDeleteDialog.razor`
- Test: `tests/SqlAgent.Tests/DatabasePageTests.cs` (create)

**Interfaces:**
- Consumes: `ConnectionTestOutcome`, `ConnectionFailureText` (Task 11); `AppState.RecordTest` / `ForgetStatus` (Task 12); `DatabaseConnectionService` (existing).
- Produces, used by Task 14: `DatabasePage` renders `<ObjectsPanel ConnectionId="..." />` once a test has succeeded. Task 14 creates that component; this task leaves a placeholder element with `data-testid="objects-panel-slot"` in its place so the "appears after a successful test" behaviour is testable now.

- [ ] **Step 1: Write the failing tests**

Create `tests/SqlAgent.Tests/DatabasePageTests.cs`:

```csharp
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SqlAgent.Core;
using SqlAgent.Host.Components.Pages;
using SqlAgent.Host.Web;
using SqlAgent.Storage;

namespace SqlAgent.Tests;

/// <summary>Provider double whose connection test is scriptable, so the page's two panels can be driven
/// through both outcomes without a live server.</summary>
file sealed class PageFakeProvider(DatabaseProviderType type) : IDatabaseProvider
{
    public Func<ConnectionTestResult> Result { get; set; } = () => ConnectionTestResult.Ok("PostgreSQL 16.0", 12);
    public DatabaseProviderType ProviderType => type;

    public Task<ConnectionTestResult> TestConnectionAsync(string cs, CancellationToken ct = default)
        => Task.FromResult(Result());

    public Task<DatabaseSchema> GetSchemaAsync(string cs, CancellationToken ct = default)
        => Task.FromResult(new DatabaseSchema(
            [new SchemaTable("dbo", "orders", [new SchemaColumn("id", "int", false)], ["id"], [], [])]));

    public Task<QueryResultSet> ExecuteQueryAsync(
        string cs, string sql, QueryExecutionOptions options, CancellationToken ct = default)
        => throw new NotSupportedException();
}

public class DatabasePageTests : IDisposable
{
    private readonly SqliteConnection _conn = new("DataSource=:memory:");
    private readonly Bunit.TestContext _ctx = new();
    private readonly PageFakeProvider _provider = new(DatabaseProviderType.Postgres);

    public DatabasePageTests()
    {
        _conn.Open();
        _ctx.Services.AddDbContext<SqlAgentDbContext>(o => o.UseSqlite(_conn));
        _ctx.Services.AddScoped<ISecretStore, InMemorySecretStore>();
        _ctx.Services.AddScoped<DatabaseConnectionService>();
        _ctx.Services.AddScoped<TablePolicyService>();
        _ctx.Services.AddScoped<ConnectionTester>();
        _ctx.Services.AddSingleton<IDatabaseProvider>(_provider);
        _ctx.Services.AddSingleton<IDatabaseProviderRegistry, DatabaseProviderRegistry>();
        // ConnectionTester, ConnectionPanel, and ObjectsPanel all inject ILogger<T>. AddLogging registers
        // the open generic once; a NullLogger<ConnectionTester>.Instance registration would satisfy only
        // that one closed type and leave the components unresolvable.
        _ctx.Services.AddLogging();
        _ctx.Services.AddScoped<ScopedRunner>();
        _ctx.Services.AddScoped<AppState>();
        _ctx.Services.AddScoped<DialogService>();
        _ctx.Services.AddScoped<ShortcutService>();
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        using var scope = _ctx.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<SqlAgentDbContext>().Database.EnsureCreated();
    }

    public void Dispose() { _ctx.Dispose(); _conn.Dispose(); }

    private async Task<Guid> SeedAsync(string name = "warehouse")
    {
        using var scope = _ctx.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<DatabaseConnectionService>();
        return (await svc.CreateAsync(
            new DatabaseConnectionInput(name, DatabaseProviderType.Postgres, true), "cs")).Id;
    }

    private IRenderedComponent<DatabasePage> Render(Guid? id = null)
        => _ctx.RenderComponent<DatabasePage>(p => p.Add(x => x.Id, id));

    [Fact]
    public void A_new_database_starts_with_an_empty_form_and_no_objects_panel()
    {
        var page = Render();

        Assert.Equal("", page.Find("[data-testid=connection-name]").GetAttribute("value") ?? "");
        Assert.Empty(page.FindAll("[data-testid=objects-panel-slot]"));
        // Nothing to delete yet, so offering it would be a button that can only fail.
        Assert.Empty(page.FindAll("[data-testid=connection-delete]"));
    }

    [Fact]
    public async Task Opening_a_saved_database_tests_it_and_shows_the_objects_panel()
    {
        // Requiring a manual click on every visit would put a button between the user and the thing they
        // navigated to. A new connection still has to be tested by hand — there is nothing to test yet.
        var id = await SeedAsync();

        var page = Render(id);

        Assert.Contains("warehouse", page.Find("[data-testid=connection-name]").GetAttribute("value"));
        Assert.Single(page.FindAll("[data-testid=objects-panel-slot]"));
    }

    [Fact]
    public async Task The_connection_string_field_is_blank_when_editing()
    {
        // The secret is never sent to the browser, and blank on save means "keep what is stored". Both
        // rules are inherited verbatim from the page this one replaces.
        var id = await SeedAsync();

        var page = Render(id);

        Assert.Equal("", page.Find("[data-testid=connection-string]").GetAttribute("value") ?? "");
        Assert.Contains("keep", page.Find("[data-testid=connection-string]").GetAttribute("placeholder"),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_failed_test_hides_the_objects_panel_and_shows_a_classified_outcome()
    {
        var id = await SeedAsync();
        _provider.Result = () => ConnectionTestResult.Fail(
            ConnectionFailure.AuthenticationFailed, "28P01: password authentication failed", 4);

        var page = Render(id);

        Assert.Empty(page.FindAll("[data-testid=objects-panel-slot]"));
        Assert.Contains("connection_test_auth_failed", page.Markup);
        Assert.Contains("rejected the credentials", page.Markup);
    }

    [Fact]
    public async Task A_failed_test_never_renders_the_driver_text()
    {
        // The whole point of the classification. This is the assertion that would have caught the old
        // page rendering ex.Message straight through.
        var id = await SeedAsync();
        _provider.Result = () => ConnectionTestResult.Fail(
            ConnectionFailure.AuthenticationFailed,
            "28P01: password authentication failed for user \"sa\" (Host=secret-host;Password=hunter2)", 4);

        var page = Render(id);

        Assert.DoesNotContain("hunter2", page.Markup);
        Assert.DoesNotContain("secret-host", page.Markup);
        Assert.DoesNotContain("28P01", page.Markup);
    }

    [Fact]
    public async Task A_successful_test_reports_the_server_version_and_the_elapsed_time()
    {
        var id = await SeedAsync();

        var page = Render(id);

        Assert.Contains("PostgreSQL 16.0", page.Markup);
        Assert.Contains("12", page.Markup);
    }

    [Fact]
    public async Task A_test_records_the_dot_state_for_the_sidebar()
    {
        var id = await SeedAsync();
        var state = _ctx.Services.GetRequiredService<AppState>();

        Render(id);

        Assert.Equal(ConnectionStatus.Ok, state.StatusOf(id));
    }

    [Fact]
    public void Saving_a_new_database_without_a_connection_string_is_refused_with_a_code()
    {
        var page = Render();

        page.Find("[data-testid=connection-name]").Change("warehouse");
        page.Find("[data-testid=connection-save]").Click();

        Assert.Contains("connection_string_required", page.Markup);
    }

    [Fact]
    public async Task Saving_announces_the_change_so_the_sidebar_re_reads()
    {
        var page = Render();
        var state = _ctx.Services.GetRequiredService<AppState>();
        var raised = 0;
        state.ConnectionsChanged += () => raised++;

        page.Find("[data-testid=connection-name]").Change("warehouse");
        page.Find("[data-testid=connection-string]").Change("Host=localhost");
        await page.Find("[data-testid=connection-save]").ClickAsync(new MouseEventArgs());

        Assert.Equal(1, raised);
        using var scope = _ctx.Services.CreateScope();
        Assert.Single(await scope.ServiceProvider.GetRequiredService<DatabaseConnectionService>().ListAsync());
    }

    [Fact]
    public async Task Deleting_goes_through_a_confirmation()
    {
        // The one destructive operation on this page. Every other destructive operation in the app asks
        // first; the page this replaces deleted a connection on a single click with no question at all.
        var id = await SeedAsync();
        var page = Render(id);
        var dialogs = _ctx.Services.GetRequiredService<DialogService>();

        await page.Find("[data-testid=connection-delete]").ClickAsync(new MouseEventArgs());

        Assert.NotNull(dialogs.Current);
        using var scope = _ctx.Services.CreateScope();
        Assert.Single(await scope.ServiceProvider.GetRequiredService<DatabaseConnectionService>().ListAsync());
    }
}
```

- [ ] **Step 2: Run them and watch them fail**

Run: `dotnet test tests/SqlAgent.Tests/SqlAgent.Tests.csproj --filter "FullyQualifiedName~DatabasePageTests"`
Expected: FAIL to compile — `DatabasePage` does not exist.

- [ ] **Step 3: Write the delete dialog**

Create `src/SqlAgent.Host/Components/Shared/Database/ConnectionDeleteDialog.razor`:

```razor
@* The only destructive operation on the config page. The page it replaces deleted a connection on a
   single click and never asked, which left it alone among this application's destructive operations. *@
<Modal Title="Delete database" OnClose="OnCancel">
    <ChildContent>
        <p>Delete <strong>@Name</strong>?</p>
        <p class="muted">
            Its connection string and every access level you set for it are removed. Chats that used it
            keep their transcript — the database name is recorded in each message.
        </p>
    </ChildContent>
    <Footer>
        <button type="button" data-testid="connection-delete-cancel" @onclick="OnCancel">Cancel</button>
        <button type="button" class="danger" data-testid="connection-delete-confirm"
                @onclick="OnConfirm">Delete</button>
    </Footer>
</Modal>

@code {
    [Parameter, EditorRequired] public string Name { get; set; } = "";
    [Parameter] public EventCallback OnConfirm { get; set; }
    [Parameter] public EventCallback OnCancel { get; set; }
}
```

- [ ] **Step 4: Write the connection panel**

Create `src/SqlAgent.Host/Components/Shared/Database/ConnectionPanel.razor`:

```razor
@inject ScopedRunner Runner
@inject AppState State
@inject DialogService Dialogs
@inject NavigationManager Nav
@inject ILogger<ConnectionPanel> Logger

<section class="panel">
    <h2>Connection</h2>

    <div class="field">
        <label for="connection-name">Name</label>
        <input id="connection-name" data-testid="connection-name" value="@_name" @onchange="OnNameChanged" />
    </div>

    <div class="field">
        <label for="connection-provider">DBMS</label>
        <select id="connection-provider" data-testid="connection-provider" @onchange="OnProviderChanged">
            @foreach (var p in Enum.GetValues<DatabaseProviderType>())
            {
                <option value="@p" selected="@(p == _provider)">@Label(p)</option>
            }
        </select>
    </div>

    <div class="field">
        <label for="connection-string">Connection string</label>
        @* Never populated from the server, on either path: the secret does not travel to the browser,
           and blank on save means "keep the stored one". Both rules are carried over verbatim from the
           page this replaces. *@
        <input id="connection-string" data-testid="connection-string" type="password"
               value="@_connectionString" @onchange="OnConnectionStringChanged"
               placeholder="@(Id is null ? "" : "leave blank to keep the stored secret")" />
    </div>

    <div class="field field-inline">
        <input id="connection-readonly" type="checkbox" data-testid="connection-readonly"
               checked="@_isReadOnly" @onchange="OnReadOnlyChanged" />
        <label for="connection-readonly">Read-only</label>
    </div>

    <div class="actions">
        <button type="button" data-testid="connection-test" @onclick="TestAsync" disabled="@_busy">Test</button>
        <button type="button" class="primary" data-testid="connection-save" @onclick="SaveAsync" disabled="@_busy">Save</button>
        @if (Id is not null)
        {
            <button type="button" class="danger" data-testid="connection-delete" @onclick="ShowDelete">Delete</button>
        }
    </div>

    @if (_status is not null)
    {
        <OutcomeMessage Code="@_statusCode" Message="@_status" />
    }
</section>

@code {
    [Parameter] public Guid? Id { get; set; }

    /// <summary>Raised with true when a test succeeds and false when it does not, so the page can show or
    /// hide the panels that need a reachable database.</summary>
    [Parameter] public EventCallback<bool> OnTested { get; set; }

    /// <summary>Raised after a create, so the page can move to the saved database's own route.</summary>
    [Parameter] public EventCallback<Guid> OnCreated { get; set; }

    private string _name = "";
    private DatabaseProviderType _provider = DatabaseProviderType.Postgres;
    private bool _isReadOnly = true;
    private string _connectionString = "";
    private bool _busy;
    private string? _status;
    private string? _statusCode;
    private Guid? _loaded;

    private static string Label(DatabaseProviderType type) => type switch
    {
        DatabaseProviderType.SqlServer => "SQL Server",
        DatabaseProviderType.Postgres => "Postgres",
        _ => type.ToString(),
    };

    // OnParametersSetAsync, not OnInitializedAsync: navigating between two /database/{id} routes reuses
    // this component instance, so initialization alone would leave the previous database's values in the
    // form. Guarded on _loaded so an unrelated re-render does not discard what the user has typed.
    protected override async Task OnParametersSetAsync()
    {
        if (_loaded == Id && (Id is not null || _loaded is null && _name.Length > 0)) return;
        _loaded = Id;
        Clear();
        _connectionString = "";

        if (Id is not { } id)
        {
            _name = "";
            _provider = DatabaseProviderType.Postgres;
            _isReadOnly = true;
            await OnTested.InvokeAsync(false);
            return;
        }

        var info = await Runner.RunAsync<DatabaseConnectionService, DatabaseConnectionInfo?>(s => s.GetAsync(id));
        if (info is null)
        {
            Refuse("connection_not_found", "That database is no longer configured.");
            await OnTested.InvokeAsync(false);
            return;
        }

        _name = info.Name;
        _provider = info.ProviderType;
        _isReadOnly = info.IsReadOnly;

        // Opening a saved database tests it once, so the Objects panel is present on arrival. A test per
        // visit is cheap; a button between the user and the thing they navigated to is not.
        await TestAsync();
    }

    private void OnNameChanged(ChangeEventArgs e) => _name = e.Value?.ToString() ?? "";
    private void OnConnectionStringChanged(ChangeEventArgs e) => _connectionString = e.Value?.ToString() ?? "";
    private void OnReadOnlyChanged(ChangeEventArgs e) => _isReadOnly = (bool)(e.Value ?? false);

    private void OnProviderChanged(ChangeEventArgs e)
    {
        if (Enum.TryParse<DatabaseProviderType>(e.Value?.ToString(), out var parsed))
            _provider = parsed;
    }

    private async Task TestAsync()
    {
        _busy = true;
        try
        {
            ConnectionTestOutcome? outcome;
            if (Id is { } id && string.IsNullOrWhiteSpace(_connectionString))
                outcome = await Runner.RunAsync<ConnectionTester, ConnectionTestOutcome?>(t => t.TestSavedAsync(id));
            else if (!string.IsNullOrWhiteSpace(_connectionString))
                outcome = await Runner.RunAsync<ConnectionTester, ConnectionTestOutcome?>(
                    t => t.TestDraftAsync(_provider, _connectionString)!);
            else
            {
                Refuse("connection_string_required", "Enter a connection string, then test it.");
                await OnTested.InvokeAsync(false);
                return;
            }

            if (outcome is null)
            {
                Refuse("connection_secret_missing", "The database or its stored secret is missing.");
                await OnTested.InvokeAsync(false);
                return;
            }

            if (Id is { } tested) State.RecordTest(tested, outcome.Success);

            if (outcome.Success)
            {
                Succeed($"Connected. {outcome.ServerVersion}, {outcome.ElapsedMs} ms.");
                await OnTested.InvokeAsync(true);
            }
            else
            {
                // The classified outcome, never the driver's own sentence — ConnectionTester logged that
                // and did not return it, so there is nothing here that could leak.
                Refuse(ConnectionFailureText.Code(outcome.Failure), ConnectionFailureText.Message(outcome.Failure));
                await OnTested.InvokeAsync(false);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to test connection {ConnectionId}.", Id);
            Fail("The database could not be tested. The details are in the server log.");
            await OnTested.InvokeAsync(false);
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task SaveAsync()
    {
        if (string.IsNullOrWhiteSpace(_name))
        {
            Refuse("connection_name_required", "A name is required.");
            return;
        }

        var input = new DatabaseConnectionInput(_name, _provider, _isReadOnly);
        _busy = true;
        try
        {
            if (Id is { } id)
            {
                var secret = string.IsNullOrWhiteSpace(_connectionString) ? null : _connectionString;
                await Runner.RunAsync<DatabaseConnectionService>(s => s.UpdateAsync(id, input, secret));
                _connectionString = "";
                Succeed("Saved.");
                State.NotifyConnectionsChanged();
            }
            else
            {
                if (string.IsNullOrWhiteSpace(_connectionString))
                {
                    // A refusal, not a failure: it carries a code like every other refusal in the UI.
                    Refuse("connection_string_required", "A connection string is required for a new database.");
                    return;
                }
                var created = await Runner.RunAsync<DatabaseConnectionService, DatabaseConnectionInfo>(
                    s => s.CreateAsync(input, _connectionString));
                _connectionString = "";
                Succeed("Saved.");
                State.NotifyConnectionsChanged();
                await OnCreated.InvokeAsync(created.Id);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to save connection {ConnectionId}.", Id);
            Fail("The database could not be saved. The details are in the server log.");
        }
        finally
        {
            _busy = false;
        }
    }

    private void ShowDelete()
    {
        if (Dialogs.Current is not null) return;
        Dialogs.Show(@<ConnectionDeleteDialog Name="@_name" OnCancel="Dialogs.Close" OnConfirm="DeleteAsync" />);
    }

    private async Task DeleteAsync()
    {
        Dialogs.Close();
        if (Id is not { } id) return;
        try
        {
            await Runner.RunAsync<DatabaseConnectionService>(s => s.DeleteAsync(id));
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to delete connection {ConnectionId}.", id);
            Fail("The database could not be deleted. The details are in the server log.");
            return;
        }

        // Both, and in this order: the dot must not outlive the row it belonged to, and the sidebar has
        // to lose the row before this component navigates away from it.
        State.ForgetStatus(id);
        State.NotifyConnectionsChanged();
        Nav.NavigateTo("/database");
    }

    private void Clear() { _status = null; _statusCode = null; }
    private void Succeed(string message) { _status = message; _statusCode = null; }

    /// <summary>An expected outcome the user should read as deliberate: shown with its stable code.</summary>
    private void Refuse(string code, string message) { _status = message; _statusCode = code; }

    /// <summary>Something broke. No code — a code would make it read as a rule the app applied on purpose.</summary>
    private void Fail(string message) { _status = message; _statusCode = null; }
}
```

- [ ] **Step 5: Write the page**

Create `src/SqlAgent.Host/Components/Pages/DatabasePage.razor`:

```razor
@* One directive, not two. An optional route parameter already matches the bare segment, so declaring
   "/database" beside it registers two routes for the same URL and the router throws on an ambiguous
   match rather than picking one. *@
@page "/database/{Id:guid?}"
@inject NavigationManager Nav

@* Panels stack rather than sitting side by side: the objects list is long and the connection form is
   short, and a two-column split would make the form's whitespace grow with the database's size. *@
<h1>@(Id is null ? "New database" : "Database")</h1>

<ConnectionPanel Id="Id" OnTested="OnTested" OnCreated="OnCreated" />

@if (_connected && Id is { } id)
{
    <div data-testid="objects-panel-slot">
        @* Task 14 replaces this with <ObjectsPanel ConnectionId="id" />. The slot exists now so the
           "appears only after a successful test" rule is under test before the panel is built. *@
    </div>
}

@code {
    [Parameter] public Guid? Id { get; set; }

    private bool _connected;

    // Reset on every parameter change, not only on a failed test: navigating from a reachable database to
    // an unreachable one must not leave the previous one's panel on screen while the new test is still in
    // flight.
    protected override void OnParametersSet() => _connected = false;

    private void OnTested(bool success) => _connected = success;

    // The route carries the identity, so a created database has to move to its own URL — otherwise Save
    // followed by Test would test a draft that no longer exists anywhere but in this form.
    private void OnCreated(Guid id) => Nav.NavigateTo($"/database/{id}");
}
```

These rules go in **`src/SqlAgent.Host/wwwroot/css/app.css`, not a scoped stylesheet.** A scoped
`.razor.css` compiles against the markup of its own component, and `.panel`/`.field`/`.actions` are
rendered by `ConnectionPanel` and (in Task 14) `ObjectsPanel` — two other components. Put them once, in
the shared sheet, under a comment naming both panels as the users:

```css
/* ============================================================================
   Database config page. These live here rather than in a scoped stylesheet
   because ConnectionPanel and ObjectsPanel both render them, and a scoped sheet
   cannot reach another component's markup.
   ========================================================================== */
.panel {
  border: 1px solid var(--border-100);
  border-radius: var(--radius-panel);
  padding: var(--space-4);
  margin-bottom: var(--space-4);
}

.field { display: flex; flex-direction: column; gap: var(--space-1); margin-bottom: var(--space-3); }
.field-inline { flex-direction: row; align-items: center; gap: var(--space-2); }
.actions { display: flex; gap: var(--space-2); flex-wrap: wrap; }
```

Check `--border-100` and `--radius-panel` against the token block at the top of `app.css` and use whatever
it actually declares; do not introduce a token. `DesignSystemTests` reads this file, so a new rule that
references a token nobody defined will show up there rather than in the browser.

No `DatabasePage.razor.css` is created — the page itself renders only a heading and two child components,
and an empty scoped sheet is a file the next reader has to open to discover it says nothing.

- [ ] **Step 6: Run the tests and watch them pass**

Run: `dotnet test tests/SqlAgent.Tests/SqlAgent.Tests.csproj --filter "FullyQualifiedName~DatabasePageTests"`
Expected: PASS, 10 tests.

- [ ] **Step 7: Run the full suite**

Run: `dotnet test tests/SqlAgent.Tests/SqlAgent.Tests.csproj`
Expected: PASS.

- [ ] **Step 8: Commit**

```bash
git add src/SqlAgent.Host/Components tests/SqlAgent.Tests/DatabasePageTests.cs
git commit -m "Add the database config page and its connection panel"
```

---

## Task 14: The objects panel

**Files:**
- Create: `src/SqlAgent.Host/Components/Shared/Database/ObjectsPanel.razor`
- Create: `src/SqlAgent.Host/Components/Shared/Database/ObjectsPanel.razor.css`
- Modify: `src/SqlAgent.Host/Components/Pages/DatabasePage.razor` (replace the slot)
- Test: `tests/SqlAgent.Tests/ObjectsPanelTests.cs` (create)

**Interfaces:**
- Consumes: `TablePolicyService.ListObjectsAsync` / `SetAccessAsync` / `SetSchemaAccessAsync`, `DatabaseObjectPolicy`, `DatabaseObjectKind`, `SetAccessOutcome`, `ObjectAccess` (Task 9); `Segmented` and `SegmentedOption` (phase A).
- Produces: `<ObjectsPanel ConnectionId="Guid" />`.

- [ ] **Step 1: Write the failing tests**

Create `tests/SqlAgent.Tests/ObjectsPanelTests.cs`:

```csharp
using Bunit;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SqlAgent.Core;
using SqlAgent.Core.Policy;
using SqlAgent.Host.Components.Shared.Database;
using SqlAgent.Host.Web;
using SqlAgent.Storage;

namespace SqlAgent.Tests;

file sealed class ObjectsFakeProvider(DatabaseProviderType type) : IDatabaseProvider
{
    public DatabaseProviderType ProviderType => type;

    public Task<ConnectionTestResult> TestConnectionAsync(string cs, CancellationToken ct = default)
        => Task.FromResult(ConnectionTestResult.Ok(null, 0));

    public Task<DatabaseSchema> GetSchemaAsync(string cs, CancellationToken ct = default)
        => Task.FromResult(new DatabaseSchema(
            [new SchemaTable("dbo", "orders", [new SchemaColumn("id", "int", false)], ["id"], [], []),
             new SchemaTable("dbo", "customers", [new SchemaColumn("id", "int", false)], [], [], []),
             new SchemaTable("sales", "leads", [new SchemaColumn("id", "int", false)], [], [], [])],
            [new SchemaView("dbo", "order_summary", [new SchemaColumn("id", "int", false)])]));

    public Task<QueryResultSet> ExecuteQueryAsync(
        string cs, string sql, QueryExecutionOptions options, CancellationToken ct = default)
        => throw new NotSupportedException();
}

public class ObjectsPanelTests : IDisposable
{
    private readonly SqliteConnection _conn = new("DataSource=:memory:");
    private readonly Bunit.TestContext _ctx = new();

    public ObjectsPanelTests()
    {
        _conn.Open();
        _ctx.Services.AddDbContext<SqlAgentDbContext>(o => o.UseSqlite(_conn));
        _ctx.Services.AddScoped<ISecretStore, InMemorySecretStore>();
        _ctx.Services.AddScoped<DatabaseConnectionService>();
        _ctx.Services.AddScoped<TablePolicyService>();
        _ctx.Services.AddSingleton<IDatabaseProvider>(new ObjectsFakeProvider(DatabaseProviderType.Postgres));
        _ctx.Services.AddSingleton<IDatabaseProviderRegistry, DatabaseProviderRegistry>();
        _ctx.Services.AddLogging();
        _ctx.Services.AddScoped<ScopedRunner>();
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        using var scope = _ctx.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<SqlAgentDbContext>().Database.EnsureCreated();
    }

    public void Dispose() { _ctx.Dispose(); _conn.Dispose(); }

    private async Task<Guid> SeedAsync()
    {
        using var scope = _ctx.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<DatabaseConnectionService>();
        return (await svc.CreateAsync(
            new DatabaseConnectionInput("c", DatabaseProviderType.Postgres, false), "cs")).Id;
    }

    private async Task<IRenderedComponent<ObjectsPanel>> RenderAsync()
    {
        var id = await SeedAsync();
        return _ctx.RenderComponent<ObjectsPanel>(p => p.Add(x => x.ConnectionId, id));
    }

    private async Task<ObjectAccess> LevelAsync(string name)
    {
        using var scope = _ctx.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SqlAgentDbContext>();
        var row = await db.TablePolicies.SingleOrDefaultAsync(p => p.TableName == name);
        return row is null ? ObjectAccess.Full
            : !row.IsVisible ? ObjectAccess.Hidden
            : row.CanWrite ? ObjectAccess.Full
            : ObjectAccess.ReadOnly;
    }

    [Fact]
    public async Task Objects_are_grouped_under_their_schema()
    {
        var panel = await RenderAsync();

        var headers = panel.FindAll(".schema-header").Select(h => h.TextContent).ToList();
        Assert.Contains(headers, h => h.Contains("dbo"));
        Assert.Contains(headers, h => h.Contains("sales"));
    }

    [Fact]
    public async Task Groups_start_collapsed_when_there_is_more_than_one_schema()
    {
        // The answer to a database with hundreds of objects, in place of a virtualized list: nothing
        // renders until the user says which schema they came for.
        var panel = await RenderAsync();

        Assert.Empty(panel.FindAll(".object-row"));

        await panel.FindAll("[data-testid^=schema-toggle-]").First().ClickAsync(new MouseEventArgs());
        Assert.NotEmpty(panel.FindAll(".object-row"));
    }

    [Fact]
    public async Task A_view_is_labelled_and_offers_two_levels_rather_than_three()
    {
        var panel = await RenderAsync();
        await panel.Find("[data-testid=schema-toggle-dbo]").ClickAsync(new MouseEventArgs());

        var row = panel.Find("[data-testid=object-row-dbo-order_summary]");
        Assert.Contains("View", row.TextContent);
        Assert.Equal(2, row.QuerySelectorAll(".segment").Length);
    }

    [Fact]
    public async Task A_table_offers_all_three_levels()
    {
        var panel = await RenderAsync();
        await panel.Find("[data-testid=schema-toggle-dbo]").ClickAsync(new MouseEventArgs());

        Assert.Equal(3, panel.Find("[data-testid=object-row-dbo-orders]").QuerySelectorAll(".segment").Length);
    }

    [Fact]
    public async Task Choosing_a_level_writes_it_immediately()
    {
        // No Save button: the rail this replaces wrote on every toggle, and a panel that batches changes
        // needs a story for what happens when the user navigates away mid-edit.
        var panel = await RenderAsync();
        await panel.Find("[data-testid=schema-toggle-dbo]").ClickAsync(new MouseEventArgs());

        var readOnly = panel.Find("[data-testid=object-row-dbo-orders] .segment:nth-child(2)");
        await readOnly.ClickAsync(new MouseEventArgs());

        Assert.Equal(ObjectAccess.ReadOnly, await LevelAsync("orders"));
    }

    [Fact]
    public async Task A_schema_header_sets_every_object_beneath_it()
    {
        var panel = await RenderAsync();

        await panel.Find("[data-testid=schema-set-dbo] .segment:first-child").ClickAsync(new MouseEventArgs());

        Assert.Equal(ObjectAccess.Hidden, await LevelAsync("orders"));
        Assert.Equal(ObjectAccess.Hidden, await LevelAsync("customers"));
        Assert.Equal(ObjectAccess.Hidden, await LevelAsync("order_summary"));
        Assert.Equal(ObjectAccess.Full, await LevelAsync("leads"));
    }

    [Fact]
    public async Task The_filter_narrows_the_list_by_name()
    {
        var panel = await RenderAsync();
        await panel.Find("[data-testid=schema-toggle-dbo]").ClickAsync(new MouseEventArgs());

        panel.Find("[data-testid=objects-filter]").Input("cust");

        Assert.Single(panel.FindAll(".object-row"));
        Assert.Contains("customers", panel.Find(".object-row").TextContent);
    }

    [Fact]
    public async Task Filtering_opens_the_groups_that_still_have_matches()
    {
        // A filter that leaves every group collapsed shows the user nothing and reads as "no results".
        var panel = await RenderAsync();

        panel.Find("[data-testid=objects-filter]").Input("leads");

        Assert.Single(panel.FindAll(".object-row"));
        Assert.Contains("leads", panel.Find(".object-row").TextContent);
    }

    [Fact]
    public async Task A_filter_matching_nothing_says_so()
    {
        var panel = await RenderAsync();

        panel.Find("[data-testid=objects-filter]").Input("zzz");

        Assert.Empty(panel.FindAll(".object-row"));
        Assert.Contains("No objects match", panel.Markup);
    }

    [Fact]
    public async Task A_level_survives_a_reload_of_the_panel()
    {
        var panel = await RenderAsync();
        await panel.Find("[data-testid=schema-toggle-dbo]").ClickAsync(new MouseEventArgs());
        await panel.Find("[data-testid=object-row-dbo-orders] .segment:first-child")
            .ClickAsync(new MouseEventArgs());

        await panel.InvokeAsync(async () => await panel.Instance.ReloadAsync());
        await panel.Find("[data-testid=schema-toggle-dbo]").ClickAsync(new MouseEventArgs());

        Assert.Contains("selected",
            panel.Find("[data-testid=object-row-dbo-orders] .segment:first-child").GetAttribute("class"));
    }
}
```

- [ ] **Step 2: Run them and watch them fail**

Run: `dotnet test tests/SqlAgent.Tests/SqlAgent.Tests.csproj --filter "FullyQualifiedName~ObjectsPanelTests"`
Expected: FAIL to compile — `ObjectsPanel` does not exist.

- [ ] **Step 3: Write the panel**

Create `src/SqlAgent.Host/Components/Shared/Database/ObjectsPanel.razor`:

```razor
@inject ScopedRunner Runner
@inject ILogger<ObjectsPanel> Logger

<section class="panel">
    <div class="objects-head">
        <h2>Objects</h2>
        @* @oninput rather than @onchange: this is a round trip per keystroke, which is the same one B2
           accepted deliberately for search, and here it only filters a list already in memory. A filter
           that applies on blur is a filter people stop using. *@
        <input type="search" class="objects-filter" data-testid="objects-filter"
               placeholder="Filter by name" value="@_filter" @oninput="OnFilterChanged" />
    </div>

    @if (_error is not null)
    {
        <OutcomeMessage Message="@_error" />
    }
    else if (_objects.Count == 0)
    {
        <p class="muted">This database has no tables or views.</p>
    }
    else if (Groups().Count == 0)
    {
        <p class="muted">No objects match that filter.</p>
    }
    else
    {
        @foreach (var group in Groups())
        {
            <div class="schema-group" @key="group.Key">
                <div class="schema-header">
                    <button type="button" class="ghost schema-toggle"
                            data-testid="@($"schema-toggle-{group.Key}")"
                            aria-expanded="@IsOpen(group.Key)"
                            @onclick="() => Toggle(group.Key)">
                        @if (IsOpen(group.Key))
                        {
                            <Icon Name="chevron-down" Size="14" />
                        }
                        else
                        {
                            <Icon Name="chevron-right" Size="14" />
                        }
                        <span>@group.Key</span>
                        <span class="schema-count">@group.Count()</span>
                    </button>
                    <div data-testid="@($"schema-set-{group.Key}")">
                        <Segmented Options="TableLevels" Value=""
                                   AriaLabel="@($"Set every object in {group.Key}")"
                                   ValueChanged="v => SetSchemaAsync(group.Key, v)" />
                    </div>
                </div>

                @if (IsOpen(group.Key))
                {
                    @foreach (var o in group)
                    {
                        <div class="object-row" @key="@($"{o.Schema}.{o.Name}")"
                             data-testid="@($"object-row-{o.Schema}-{o.Name}")">
                            <span class="object-name truncate">@o.Name</span>
                            @if (o.Kind == DatabaseObjectKind.View)
                            {
                                <Badge Tone="BadgeTone.Neutral">View</Badge>
                            }
                            <Segmented Options="@(o.Kind == DatabaseObjectKind.View ? ViewLevels : TableLevels)"
                                       Value="@o.Access.ToString()"
                                       AriaLabel="@($"Access for {o.Schema}.{o.Name}")"
                                       ValueChanged="v => SetAsync(o, v)" />
                        </div>
                    }
                }
            </div>
        }
    }
</section>

@code {
    [Parameter, EditorRequired] public Guid ConnectionId { get; set; }

    private IReadOnlyList<DatabaseObjectPolicy> _objects = [];
    private readonly HashSet<string> _open = new(StringComparer.OrdinalIgnoreCase);
    private string _filter = "";
    private string? _error;
    private Guid _loaded;

    // A view can be hidden or read-only and nothing else, so it is offered two segments rather than three
    // greyed out — a control that shows a choice it will refuse is worse than one that does not show it.
    private static readonly IReadOnlyList<SegmentedOption> TableLevels =
    [
        new(nameof(ObjectAccess.Hidden), "Not visible"),
        new(nameof(ObjectAccess.ReadOnly), "Read-only"),
        new(nameof(ObjectAccess.Full), "Full access"),
    ];

    private static readonly IReadOnlyList<SegmentedOption> ViewLevels =
    [
        new(nameof(ObjectAccess.Hidden), "Not visible"),
        new(nameof(ObjectAccess.ReadOnly), "Read-only"),
    ];

    protected override async Task OnParametersSetAsync()
    {
        // The panel is reused across a navigation from one database to another, so identity has to be
        // re-read rather than initialized once.
        if (_loaded == ConnectionId) return;
        _loaded = ConnectionId;
        _filter = "";
        _open.Clear();
        await ReloadAsync();
    }

    /// <summary>Re-reads the object list. Public because the level writes call it, and because the page
    /// re-tests the connection on arrival and may want the list refreshed with it.</summary>
    public async Task ReloadAsync()
    {
        try
        {
            _objects = await Runner.RunAsync<TablePolicyService, IReadOnlyList<DatabaseObjectPolicy>?>(
                s => s.ListObjectsAsync(ConnectionId)) ?? [];
            _error = null;

            // One schema is not a choice, so opening it saves the user a click they would always make.
            var schemas = _objects.Select(o => o.Schema).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (schemas.Count == 1) _open.Add(schemas[0]);
        }
        catch (Exception ex)
        {
            // An unreachable database is ordinary for a tool pointed at arbitrary servers. The provider's
            // text is never rendered — it can echo the connection string.
            Logger.LogError(ex, "Failed to list objects for connection {ConnectionId}.", ConnectionId);
            _objects = [];
            _error = "The object list could not be read. The details are in the server log.";
        }
    }

    private void OnFilterChanged(ChangeEventArgs e) => _filter = e.Value?.ToString() ?? "";

    private List<IGrouping<string, DatabaseObjectPolicy>> Groups() =>
        _objects
            .Where(o => string.IsNullOrWhiteSpace(_filter)
                        || o.Name.Contains(_filter, StringComparison.OrdinalIgnoreCase))
            .GroupBy(o => o.Schema, StringComparer.OrdinalIgnoreCase)
            .ToList();

    // A filter that leaves every group shut shows nothing and reads as "no results", so a search opens
    // whatever still matches. Collapsed-by-default only applies to the unfiltered list.
    private bool IsOpen(string schema) => !string.IsNullOrWhiteSpace(_filter) || _open.Contains(schema);

    private void Toggle(string schema)
    {
        if (!_open.Remove(schema)) _open.Add(schema);
    }

    private async Task SetAsync(DatabaseObjectPolicy o, string level)
    {
        if (!Enum.TryParse<ObjectAccess>(level, out var access)) return;
        await WriteAsync(s => s.SetAccessAsync(ConnectionId, o.Schema, o.Name, o.Kind, access));
    }

    private async Task SetSchemaAsync(string schema, string level)
    {
        if (!Enum.TryParse<ObjectAccess>(level, out var access)) return;
        await WriteAsync(s => s.SetSchemaAccessAsync(ConnectionId, schema, access));
    }

    private async Task WriteAsync(Func<TablePolicyService, Task<SetAccessOutcome>> write)
    {
        try
        {
            var outcome = await Runner.RunAsync<TablePolicyService, SetAccessOutcome>(write);
            _error = outcome switch
            {
                SetAccessOutcome.ConnectionMissing => "That database is no longer configured.",
                SetAccessOutcome.ViewCannotBeWritable => "A view cannot be made writable.",
                _ => null,
            };
        }
        catch (Exception ex)
        {
            // Without this the control just snapped back on the next read with no explanation, and the
            // user would believe the level had been applied — the exact defect the rail's toggle had.
            Logger.LogError(ex, "Failed to set access for connection {ConnectionId}.", ConnectionId);
            _error = "The access level could not be saved. The details are in the server log.";
        }

        // Re-read either way: the store is the truth, and a failed write must not leave the control
        // showing what the user clicked.
        await ReloadAsync();
    }
}
```

Create `src/SqlAgent.Host/Components/Shared/Database/ObjectsPanel.razor.css`:

```css
.objects-head { display: flex; align-items: center; justify-content: space-between; gap: var(--space-3); }
.objects-filter { min-width: 12rem; }

.schema-group { margin-top: var(--space-3); }
.schema-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: var(--space-2);
  padding: var(--space-1) 0;
  border-bottom: 1px solid var(--border-100);
}
.schema-toggle { display: flex; align-items: center; gap: var(--space-2); }
.schema-count { color: var(--text-100); font-size: var(--text-xs); }

.object-row {
  display: flex;
  align-items: center;
  gap: var(--space-2);
  padding: var(--space-2) 0 var(--space-2) var(--space-4);
}
.object-name { flex: 1; min-width: 0; }
```

`.panel` is not repeated here — Task 13 put it in `app.css` precisely so both panels share one frame.

- [ ] **Step 4: Put the panel on the page**

In `src/SqlAgent.Host/Components/Pages/DatabasePage.razor`, replace the slot:

```razor
@if (_connected && Id is { } id)
{
    <div data-testid="objects-panel-slot">
        <ObjectsPanel ConnectionId="id" />
    </div>
}
```

The wrapper stays so `DatabasePageTests` keeps testing the visibility rule without knowing what is inside.

- [ ] **Step 5: Run the tests and watch them pass**

Run: `dotnet test tests/SqlAgent.Tests/SqlAgent.Tests.csproj --filter "FullyQualifiedName~ObjectsPanelTests"`
Expected: PASS, 10 tests.

The schema-header `Segmented` is rendered with `Value=""` on purpose: it is a set-all control, not a
display of current state, and there is no single state to show when the objects under it disagree.
`Segmented.Select` short-circuits when the clicked value equals `Value`, so an empty `Value` is also what
keeps the same header level clickable twice.

- [ ] **Step 6: Run the full suite**

Run: `dotnet test tests/SqlAgent.Tests/SqlAgent.Tests.csproj`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add src/SqlAgent.Host/Components tests/SqlAgent.Tests/ObjectsPanelTests.cs
git commit -m "Add the objects panel with per-object access levels"
```

---

## Task 15: Retire the rail, the old page, and the compatibility wrappers

**Files:**
- Delete: `src/SqlAgent.Host/Components/Layout/SchemaRail.razor`, `SchemaRail.razor.css`
- Delete: `src/SqlAgent.Host/Components/Pages/Connections.razor`, `Connections.razor.css`
- Delete: `tests/SqlAgent.Tests/SchemaRailTests.cs`, `tests/SqlAgent.Tests/ConnectionsPageTests.cs`
- Modify: `src/SqlAgent.Host/Components/Layout/Sidebar.razor`
- Modify: `src/SqlAgent.Host/Components/Pages/Workspace.razor`
- Modify: `src/SqlAgent.Host/Components/Shared/Chat/AttachmentMenu.razor:17`
- Modify: `src/SqlAgent.Host/Components/Shared/Chat/SearchDialog.razor:121`
- Modify: `src/SqlAgent.Storage/TablePolicyService.cs` (drop `TableVisibility`, `ListAsync`, `SetVisibilityAsync`)
- Test: `tests/SqlAgent.Tests/WorkspaceTests.cs`

**Interfaces:**
- Removes: `TableVisibility`, `TablePolicyService.ListAsync`, `TablePolicyService.SetVisibilityAsync`, the `/connections` route.
- `AppState.Connection` / `Select` survive, now read and written only by `/sql`.

- [ ] **Step 1: Write the failing tests**

Add to `tests/SqlAgent.Tests/WorkspaceTests.cs`:

```csharp
    [Fact]
    public async Task The_sql_page_picks_its_own_connection()
    {
        // The rail owned this picker and the rail is gone. AppState.Connection now has exactly one reader
        // and one writer, both on this page, which is why the control belongs here.
        await SeedConnectionAsync("warehouse");

        var page = _ctx.RenderComponent<Workspace>();

        Assert.Single(page.FindAll("[data-testid=sql-connection]"));
        Assert.Contains("warehouse", page.Find("[data-testid=sql-connection]").TextContent);
    }

    [Fact]
    public async Task Choosing_a_connection_reveals_the_editor()
    {
        var id = await SeedConnectionAsync("warehouse");
        var page = _ctx.RenderComponent<Workspace>();

        Assert.Contains("Select a database", page.Markup);

        page.Find("[data-testid=sql-connection]").Change(id.ToString());

        Assert.Empty(page.FindAll("p:contains('Select a database')"));
        Assert.Single(page.FindAll(".sql-editor, textarea"));
    }

    [Fact]
    public async Task The_picker_re_reads_when_the_connection_set_changes()
    {
        var page = _ctx.RenderComponent<Workspace>();
        Assert.Empty(page.FindAll("[data-testid=sql-connection] option[value]:not([value=''])"));

        await SeedConnectionAsync("warehouse");
        var state = _ctx.Services.GetRequiredService<AppState>();
        await page.InvokeAsync(state.NotifyConnectionsChanged);

        Assert.Single(page.FindAll("[data-testid=sql-connection] option[value]:not([value=''])"));
    }
```

Write `SeedConnectionAsync` in that file if it does not already exist, matching the one in
`DatabaseSectionTests`. Register `DatabaseConnectionService`, `ISecretStore`, and `AppState` in the
fixture's service collection if they are not there already.

Add to `tests/SqlAgent.Tests/ShellTests.cs` (or wherever the sidebar's contents are asserted):

```csharp
    [Fact]
    public void The_schema_rail_is_gone_and_the_databases_section_has_taken_its_place()
    {
        // Phase A's parity test insisted the rail stay until the config page existed. It exists now, so
        // this is the assertion that replaces that one — not a deletion.
        var sidebar = _ctx.RenderComponent<Sidebar>();

        Assert.DoesNotContain("Filter tables", sidebar.Markup);
        Assert.Contains("Databases", sidebar.Markup);
    }
```

- [ ] **Step 2: Run them and watch them fail**

Run: `dotnet test tests/SqlAgent.Tests/SqlAgent.Tests.csproj --filter "FullyQualifiedName~WorkspaceTests"`
Expected: FAIL — no element matches `[data-testid=sql-connection]`.

- [ ] **Step 3: Move the picker onto the SQL page**

In `src/SqlAgent.Host/Components/Pages/Workspace.razor`, add the picker above the editor and load the list:

```razor
@page "/sql"
@implements IDisposable
@inject ScopedRunner Runner
@inject AppState State

@* The tab strip is gone. Conversations live on ChatPage.razor (/), and this page is what it always was
   underneath: a plain SQL editor over the same validated execution path. It is not a temporary parking
   spot — a full-screen editor is the right shape for a long query and a wide result, and Phase D's
   ScratchPad panel on the chat page renders these same components rather than replacing them.

   The connection picker moved here in Phase C1 when the schema rail was retired. AppState.Connection had
   exactly two readers, the rail and this page; with the rail gone it has one, so the state and the
   control that sets it belong together. The Databases section in the sidebar navigates to the config
   page and deliberately does not also select — one row with two meanings is how a click becomes a
   guess. *@
<h1>SQL</h1>

<div class="field">
    <label for="sql-connection">Database</label>
    <select id="sql-connection" data-testid="sql-connection" @onchange="OnConnectionChanged">
        <option value="">— select —</option>
        @foreach (var c in _connections)
        {
            <option value="@c.Id" selected="@(c.Id == State.ConnectionId)">@c.Name</option>
        }
    </select>
</div>

@if (State.ConnectionId is null)
{
    <p>Select a database to start querying.</p>
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
```

and in `@code`:

```csharp
    private IReadOnlyList<DatabaseConnectionInfo> _connections = [];

    protected override async Task OnInitializedAsync()
    {
        State.Changed += OnStateChanged;
        // Not a substitute for Changed: the config page can create or delete a database while this page
        // is mounted, and only ConnectionsChanged says so.
        State.ConnectionsChanged += OnConnectionsChanged;
        _sql = State.TakePendingSql() ?? "";
        await ReloadConnectionsAsync();
    }

    /// <summary>
    /// Re-reads the list, then re-points AppState at the current row for whatever is selected. That
    /// second step is what makes an edit to the selected database visible: Select compares by value, so a
    /// fresh record replaces a stale one and notifies, and a deleted row resolves to null and clears the
    /// selection.
    /// </summary>
    private async Task ReloadConnectionsAsync()
    {
        _connections = await Runner.RunAsync<DatabaseConnectionService, IReadOnlyList<DatabaseConnectionInfo>>(
            s => s.ListAsync());
        if (State.ConnectionId is { } id)
            State.Select(_connections.FirstOrDefault(c => c.Id == id));
    }

    private void OnConnectionChanged(ChangeEventArgs e)
    {
        var raw = e.Value?.ToString();
        State.Select(Guid.TryParse(raw, out var id) ? _connections.FirstOrDefault(c => c.Id == id) : null);
    }

    private void OnConnectionsChanged() => InvokeAsync(async () =>
    {
        await ReloadConnectionsAsync();
        StateHasChanged();
    });

    public void Dispose()
    {
        State.Changed -= OnStateChanged;
        State.ConnectionsChanged -= OnConnectionsChanged;
    }
```

Delete the old `Dispose` that unsubscribed only from `Changed`.

- [ ] **Step 4: Delete the rail, the old page, and their tests**

```bash
git rm src/SqlAgent.Host/Components/Layout/SchemaRail.razor \
       src/SqlAgent.Host/Components/Layout/SchemaRail.razor.css \
       src/SqlAgent.Host/Components/Pages/Connections.razor \
       src/SqlAgent.Host/Components/Pages/Connections.razor.css \
       tests/SqlAgent.Tests/SchemaRailTests.cs \
       tests/SqlAgent.Tests/ConnectionsPageTests.cs
```

Remove `<SchemaRail />` and the comment above it from `src/SqlAgent.Host/Components/Layout/Sidebar.razor`.

- [ ] **Step 5: Rewrite the sidebar's keydown comment**

The comment block above the `<aside>` in `Sidebar.razor` explains why `@onkeydown` is attached
conditionally, and its worked example is `SchemaRail`'s filter input — a component that no longer exists.
The reasoning still holds and must not be deleted with the example; a stale justification is how a
load-bearing condition gets removed as dead weight later. Replace the paragraph that names the rail with:

```
   The handler is attached only while the drawer is open, and that condition is load-bearing rather than
   tidiness. Attaching it unconditionally makes every keystroke anywhere inside this <aside> a server
   round trip, because a keydown handler on an ancestor receives them all through bubbling — plus a
   re-render of the whole Sidebar subtree, since ComponentBase.HandleEventAsync calls StateHasChanged()
   after every callback whether or not the callback changed anything. Guarding inside OnKeyDown cannot
   prevent that; only not rendering the attribute can.

   Phase C1 removed the sidebar's last text input along with SchemaRail, so nothing in here types today.
   That is a fact about the current contents, not a reason to drop the condition: the next section added
   here with a filter box would silently reintroduce the cost, and it would be invisible until someone
   profiled it. See DrawerKeyHandler below for why it is an empty EventCallback rather than the obvious
   null.
```

- [ ] **Step 6: Retarget the two remaining links**

`src/SqlAgent.Host/Components/Shared/Chat/AttachmentMenu.razor:17` — `<a href="/connections">Add a connection</a>`
becomes `<a href="/database">Add a database</a>`.

`src/SqlAgent.Host/Components/Shared/Chat/SearchDialog.razor:121` — in `OpenAsync`, the
`SearchHitKind.Database` branch becomes `Nav.NavigateTo($"/database/{hit.TargetId}")`, so a database hit
opens that database rather than a list. `SearchHit.TargetId` already carries the connection id
(`SearchService.cs:85`); nothing in the search result needs to change.

Add a case to `tests/SqlAgent.Tests/SearchDialogTests.cs` asserting a database hit navigates to
`/database/{id}` — the file already has the navigation helpers for the chat and project cases.

Then check nothing else points at the dead route:

Run: `grep -rn "/connections" --include=*.cs --include=*.razor --include=*.md src tests docs`
Expected: only historical mentions in `docs/superpowers/plans/` and `docs/superpowers/specs/`, which are
records of what was true at the time and are left alone. Anything under `src`, `tests`, or `docs/web-ui.md`
is a live reference to fix.

- [ ] **Step 7: Drop the compatibility wrappers**

In `src/SqlAgent.Storage/TablePolicyService.cs`, delete the `TableVisibility` record and the `ListAsync`
and `SetVisibilityAsync` methods, together with their `<remarks>` blocks. They existed only for the rail.

Run: `grep -rn "TableVisibility\|SetVisibilityAsync" --include=*.cs --include=*.razor src tests`
Expected: no hits.

- [ ] **Step 8: Run the full suite**

Run: `dotnet test tests/SqlAgent.Tests/SqlAgent.Tests.csproj`
Expected: PASS.

`SidebarCollapseParityTests` and `ShellTests` assert on what the sidebar contains; `DesignSystemTests` and
`RestyleRegressionTests` scan CSS and markup and will notice two deleted stylesheets. Each failure is an
expectation to update to the new shape, not a test to delete — the one exception is an assertion that
exists solely to protect the rail, which is now genuinely obsolete and should be replaced by the
`The_schema_rail_is_gone...` test from Step 1.

- [ ] **Step 9: Commit**

```bash
git add -A src tests
git commit -m "Retire the schema rail and the connections page"
```

---

## Task 16: Return focus when a dialog is re-shown with an error

Carried from phase B2. Re-showing `NameDialog` with "name already taken" reuses the component instance, so
`firstRender` is false, `Modal`'s one focus call does not run, and the user has to click back into the
field to correct the name they just typed. It is the same class of defect that phase fixed twice, in the
one focus path that survived it.

**Files:**
- Modify: `src/SqlAgent.Host/Components/Shared/Ui/Modal.razor`
- Modify: `src/SqlAgent.Host/Components/Shared/Chat/NameDialog.razor`
- Test: `tests/SqlAgent.Tests/UiPrimitiveTests.cs`, `tests/SqlAgent.Tests/NameDialogTests.cs`

**Interfaces:**
- Produces: `Modal.FocusSignal` (`object?`) — when its value changes between renders, the dialog focuses
  its `InitialFocus` target again.

- [ ] **Step 1: Write the failing tests**

Add to `tests/SqlAgent.Tests/UiPrimitiveTests.cs`:

```csharp
    [Fact]
    public void A_modal_focuses_once_on_open_and_again_only_when_its_signal_changes()
    {
        // bUnit cannot observe document.activeElement, so what is asserted here is the decision to
        // focus — how many times the dialog asks for its target. Whether the browser honours it is a
        // manual-checklist row, and always will be.
        using var ctx = new Bunit.TestContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddScoped<ShortcutService>();

        var asked = 0;
        var modal = ctx.RenderComponent<Modal>(p => p
            .Add(m => m.Title, "t")
            .Add(m => m.FocusSignal, 0)
            .Add(m => m.InitialFocus, () => { asked++; return default; }));

        Assert.Equal(1, asked);

        modal.SetParametersAndRender(p => p.Add(m => m.FocusSignal, 0));
        Assert.Equal(1, asked);

        modal.SetParametersAndRender(p => p.Add(m => m.FocusSignal, 1));
        Assert.Equal(2, asked);
    }
```

Add to `tests/SqlAgent.Tests/NameDialogTests.cs`:

```csharp
    [Fact]
    public void An_arriving_error_asks_the_dialog_to_focus_the_field_again()
    {
        // The defect this closes: the caller re-shows the dialog with an error, Blazor reuses the
        // instance, firstRender is false, and focus stays wherever the failed Save left it.
        using var ctx = new Bunit.TestContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddScoped<ShortcutService>();

        var dialog = ctx.RenderComponent<NameDialog>(p => p.Add(d => d.Title, "New project"));
        var before = dialog.FindComponent<Modal>().Instance.FocusSignal;

        dialog.SetParametersAndRender(p => p.Add(d => d.Error, "That name is already taken."));

        Assert.NotEqual(before, dialog.FindComponent<Modal>().Instance.FocusSignal);
        Assert.Contains("already taken", dialog.Markup);
    }

    [Fact]
    public void An_unchanged_error_does_not_keep_asking()
    {
        // A re-render for any other reason — a keystroke elsewhere, a parent's state change — must not
        // yank focus back into the field while the user is somewhere else in the dialog.
        using var ctx = new Bunit.TestContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddScoped<ShortcutService>();

        var dialog = ctx.RenderComponent<NameDialog>(p => p
            .Add(d => d.Title, "New project")
            .Add(d => d.Error, "That name is already taken."));
        var after = dialog.FindComponent<Modal>().Instance.FocusSignal;

        dialog.SetParametersAndRender(p => p.Add(d => d.Error, "That name is already taken."));

        Assert.Equal(after, dialog.FindComponent<Modal>().Instance.FocusSignal);
    }
```

- [ ] **Step 2: Run them and watch them fail**

Run: `dotnet test tests/SqlAgent.Tests/SqlAgent.Tests.csproj --filter "FullyQualifiedName~NameDialogTests|FullyQualifiedName~A_modal_focuses_once"`
Expected: FAIL to compile — `Modal` has no `FocusSignal`.

- [ ] **Step 3: Let Modal be asked again**

In `src/SqlAgent.Host/Components/Shared/Ui/Modal.razor`, add the parameter beside `InitialFocus`:

```csharp
    /// <summary>
    /// Changes value when the caller wants focus moved to <see cref="InitialFocus"/> again, on a render
    /// that is not the first. A dialog re-shown with a validation error is the case: DialogService swaps
    /// the render fragment, Blazor sees the same component type at the same position and reuses the
    /// instance, so firstRender is false and the one focus call below never runs — leaving the user to
    /// click back into the field to correct the name they just typed.
    ///
    /// An opaque object rather than a bool or a counter type, so a caller can signal with whatever it
    /// already has. Comparison is Equals, so a boxed int works and so does a string.
    ///
    /// This stays the only FocusAsync call a dialog makes. The alternative — letting NameDialog focus its
    /// own field from its own OnAfterRenderAsync — is exactly what SearchDialog used to do, and the two
    /// calls raced with this component's winning silently.
    /// </summary>
    [Parameter] public object? FocusSignal { get; set; }

    private object? _lastFocusSignal;
```

and replace `OnAfterRenderAsync`:

```csharp
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        var signalChanged = !Equals(FocusSignal, _lastFocusSignal);
        if (!firstRender && !signalChanged) return;

        // Recorded before the await, not after: a failed focus must not leave the dialog trying again on
        // every subsequent render.
        _lastFocusSignal = FocusSignal;

        try
        {
            await (InitialFocus?.Invoke() ?? _closeButton).FocusAsync();
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException or OperationCanceledException)
        {
            Logger.Log(ex is JSException ? LogLevel.Warning : LogLevel.Debug, ex,
                "Focusing the dialog failed; typing will require a manual click into it.");
        }
    }
```

- [ ] **Step 4: Raise the signal when an error arrives**

In `src/SqlAgent.Host/Components/Shared/Chat/NameDialog.razor`, pass the signal:

```razor
<Modal Title="@Title" OnClose="OnCancel" InitialFocus="() => _valueInput" FocusSignal="_focusSignal">
```

and add:

```csharp
    private int _focusSignal;
    private string? _lastError;

    // On an arriving error only, not on one clearing: an error clears as the dialog closes or as the
    // caller re-opens it fresh, and both already focus through firstRender.
    protected override void OnParametersSet()
    {
        if (Error == _lastError) return;
        _lastError = Error;
        if (!string.IsNullOrWhiteSpace(Error)) _focusSignal++;
    }
```

`OnInitialized` already sets `_value` from `InitialValue`; leave it alone. Do not move that assignment
into `OnParametersSet` — the caller re-shows this dialog with the user's typing still in `_value`, and
re-seeding it from `InitialValue` would discard the name they are trying to correct.

- [ ] **Step 5: Run the tests and watch them pass**

Run: `dotnet test tests/SqlAgent.Tests/SqlAgent.Tests.csproj --filter "FullyQualifiedName~NameDialogTests|FullyQualifiedName~A_modal_focuses_once"`
Expected: PASS.

- [ ] **Step 6: Run the full suite**

Run: `dotnet test tests/SqlAgent.Tests/SqlAgent.Tests.csproj`
Expected: PASS. `SearchDialogTests` and `DialogHostTests` exercise the same `Modal` — a failure there means
the signal is firing on renders it should not.

- [ ] **Step 7: Commit**

```bash
git add src/SqlAgent.Host/Components/Shared/Ui/Modal.razor src/SqlAgent.Host/Components/Shared/Chat/NameDialog.razor tests/SqlAgent.Tests
git commit -m "Return focus to the field when a dialog is re-shown with an error"
```

---

## Task 17: Documentation and the manual checklist

**Files:**
- Modify: `docs/web-ui.md`
- Modify: `docs/superpowers/plans/2026-08-14-web-ui-phase-c1-databases-and-objects.md` (tick the checklist below as it is walked)

- [ ] **Step 1: Bring `docs/web-ui.md` up to date**

Six places are now wrong. Fix each:

| Line | Was | Now |
|---|---|---|
| 99 | "the nav rows, the schema rail, and the user card" | the nav rows, the Databases, Projects and History sections, and the user card |
| 160 | "**Connections** (`/connections`) — create, edit, test, and delete database connections." | **Databases** (`/database`, `/database/{id}`) — create, edit, test, and delete a database, and set a per-object access level for each of its tables and views. |
| 171 | "The sidebar's schema rail … lists every table for the selected connection" | The `/sql` page picks its own database; the sidebar's Databases section navigates to the config page and does not select. |
| 243 | "a database match goes to Connections" | a database match opens that database's config page |
| 270-271 | manual rows naming Connections and the rail | rewrite against the new surfaces |
| 317-320 | the "Schema detail in the rail" deferral note | it is no longer deferred — the objects panel groups by schema and labels views. Replace the note with a sentence saying which phase delivered it, rather than deleting the record. |

Add a short section documenting the three access levels, the two new deny codes, and the rule that an
object with no policy row is fully accessible. That rule is the phase's central decision and the one a
reader is most likely to get backwards from the entity's own `CanWrite = false` default.

- [ ] **Step 2: Run the whole suite one more time**

Run: `dotnet test tests/SqlAgent.Tests/SqlAgent.Tests.csproj`
Expected: PASS, with no skipped tests other than the env-gated provider integration ones.

- [ ] **Step 3: Build clean**

Run: `dotnet build -warnaserror`
Expected: no warnings. A widened record or a deleted component often leaves an unused `using` behind.

- [ ] **Step 4: Walk the manual checklist**

Start the host, open a browser, and work through [Manual checklist](#manual-checklist) below. Tick each
row in this file as it passes and record what actually happened for any that does not.

This is not optional and no agent can do it. Phase B2 wrote three separate, carefully reasoned claims
about focus ordering into three files; each was plausible and each was wrong, and all three were caught
by one person opening a browser and looking at `document.activeElement`. The suite still cannot see focus.

- [ ] **Step 5: Commit**

```bash
git add docs
git commit -m "Document the databases page and the access levels"
```

---

## Manual checklist

bUnit renders markup; it does not run a browser. Every row here is something the suite structurally
cannot check, with the reason stated so nobody later mistakes it for a test that was never written.

**Focus and keyboard** — no automated test replaces these: bUnit has no `document.activeElement`, and the
three focus claims phase B2 wrote from the framework's documented behaviour were all wrong.

- [ ] Open a project's rename dialog, type a name that already exists, press Save. The error appears **and
      the cursor is back in the field** — typing immediately corrects the name with no click.
- [ ] Press Escape in that same re-shown dialog. It closes.
- [ ] Delete a database, cancel the confirmation. Focus returns to the Delete button, not to the top of
      the document.
- [ ] Tab through the objects panel. Every segmented control is reachable, and the selected segment is
      visible as selected without relying on colour alone.

**The status dot** — held in the circuit, so a reload is a real part of the behaviour.

- [ ] A database never tested this session shows a neutral dot.
- [ ] Press Test on a working database: the dot turns green in the sidebar without navigating.
- [ ] Point a database at a wrong port and press Test: the dot turns red, and the message names an
      unreachable host without printing anything from the driver.
- [ ] Reload the page. Every dot is neutral again — that is correct, not a bug.

**Connection test classification** — needs real servers; the classifiers are unit-tested, the extraction
from live driver exceptions is not.

- [ ] Wrong password on Postgres → "rejected the credentials", code `connection_test_auth_failed`.
- [ ] Wrong database name on Postgres → "no database by that name".
- [ ] Wrong port → "could not be reached at that address".
- [ ] The same three against SQL Server.
- [ ] In every case, the browser shows none of the driver's own text, and the server log shows all of it.

**The objects panel at real size** — the reason virtualization was left out is a judgement, not a
measurement. This is the measurement.

- [ ] Open a database with many schemas and several hundred objects. Groups start collapsed and the page
      is responsive.
- [ ] Type in the filter. Each keystroke is a round trip; it stays comfortable.
- [ ] Expand the largest schema. It renders without a visible stall. **If it does not, record the object
      count and say so — that is the finding that turns the virtualization non-goal into work.**

**End to end**

- [ ] Set a table to Read-only, then ask the agent to update it from the SQL page: refused with
      `policy_denied_readonly_object`.
- [ ] Write to a view from the SQL page: refused with `policy_denied_view_write`.
- [ ] Hide a view, then confirm it is absent from the objects the agent is given.
- [ ] **The staleness window, which no automated test can reach.** With a database
      already configured and its schema cached, create a new view directly in the
      database, then try to write to it from the SQL page. It will be **allowed** —
      the guard reads a cache nothing invalidates on a DDL change. Confirm that
      changing any object's access level (which does invalidate) makes the same
      write refuse with `policy_denied_view_write`. This is a recorded, accepted
      limitation, not a bug to file; the row exists so somebody has seen it happen.
- [ ] Set a schema header to Not visible and confirm every object under it goes hidden — views included.
- [ ] Start the host against a store from before this release: it migrates, a `.bak` appears beside it,
      the saved databases are all still listed, and the first schema read carries views.

---

## Notes for the reviewer

Three things in this plan are judgement calls rather than mechanics, and are the places to push back.

**Reading the schema on every execution** (Task 10). Identifying a view needs the schema, so a connection
that can run a query but cannot read its own catalog is now refused with `schema_unavailable` where it
previously executed. Such a connection is already unusable for `describe_schema` and the whole
natural-language path, which is why this was accepted — but it is a behaviour change, and the alternative
is a write to a view slipping through.

**Reflection for write targets** (Task 6). `WriteTargets` finds a statement's target by property name
because SqlParserCS offers no marker for it. A package upgrade that renames the property yields an empty
write set, which `Describe` turns into "every referenced table is a write target" — over-strict rather
than silently permissive. Confirm that fallback is actually reached before accepting the reflection.

**No `ObjectKind` column** (Task 9, and a departure from the parent spec). The page and the policy path
both derive an object's kind from the live schema, so a stored column would be written by one path and
read by none. If a later phase finds a reader for it, it comes back with that reader.

