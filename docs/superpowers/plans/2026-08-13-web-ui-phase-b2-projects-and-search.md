# Web UI Phase B2 — Projects, Search, and a Keyboard Shortcut

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Group chats into projects, make the whole history searchable from a modal reachable with `Ctrl`/`Cmd`+`K`, and close four debts earlier phases recorded rather than fixed.

**Architecture:** One migration adds `Project` and `Chat.ProjectId`. Two scoped services in `SqlAgent.Storage` — `ProjectService` and `SearchService` — run through the existing `ScopedRunner`. In the UI, B1's history row and rename dialog are extracted so the new project section reuses them rather than copying them, and a single document-level `keydown` listener feeds a circuit-scoped `ShortcutService`, which is what both `Ctrl`/`Cmd`+`K` and a Safari-proof Escape ride on.

**Tech Stack:** .NET 10, Blazor Server (Interactive Server on `<Routes>`), EF Core 10 + SQLite, xUnit + bUnit. No Node, no npm, no CDN.

**Spec:** `docs/superpowers/specs/2026-08-13-web-ui-phase-b2-projects-and-search-design.md`

## Global Constraints

- **Target framework `net10.0`.** Nullable enabled, implicit usings enabled.
- **No Node toolchain, no npm, no CDN.** CI runs only `dotnet restore/build/test` against `SqlAgent.slnx`. Scripts ship in `wwwroot`.
- **No component stylesheet may contain a literal color** — components consume `var(--token)` only. `RestyleRegressionTests.No_component_stylesheet_hard_codes_a_hex_color` enforces it.
- **Result rows are never persisted.** Unchanged from B1; nothing in this phase touches that path.
- **Provider and exception text is never rendered to the user.** It can echo a connection string.
- **Every existing test must stay green** unless a task names the test it changes and why. Tasks 1, 3, 4, 7 and 8 each name theirs; no other test may be touched.
- **Migrations are generated, never hand-written.** `dotnet tool restore` first if `dotnet-ef` is not on the path; the design-time factory means `dotnet ef migrations add <Name> --project src/SqlAgent.Storage` needs no startup project.
- **Test conventions:** xUnit, `Bunit.TestContext`, sentence-style test names with underscores, one class per unit under test in `tests/SqlAgent.Tests/`. Comments explain *why* a test exists, not what it does. Small sibling presentational components may share one test class, as `UiPrimitiveTests` and `ComposerTests` already do.
- **bUnit runs no browser, no CSS engine, no focus model and no JS engine.** Anything depending on those is asserted on rendered DOM structure or stylesheet source text, driven by invoking a `[JSInvokable]` directly, or moved to the manual checklist in `docs/web-ui.md`.
- **Interop-touching components** catch `JSException`, `JSDisconnectedException` and `OperationCanceledException` explicitly — they are siblings, not a base/derived chain — and log Warning for the first, Debug for the others.
- **Commit messages** end with `Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>`.
- **Verification command:** `dotnet test SqlAgent.slnx --configuration Release`

## File Structure

| File | Responsibility | Task |
|---|---|---|
| `src/SqlAgent.Storage/Project.cs` | The `Project` entity and its chats navigation | 1 |
| `src/SqlAgent.Storage/ChatEntities.cs` | `Chat` gains `ProjectId` and its navigation | 1 |
| `src/SqlAgent.Storage/SqlAgentDbContext.cs` | `Projects` set, unique `NOCASE` name, FK with `Restrict` | 1 |
| `src/SqlAgent.Storage/Migrations/*_Projects.cs` | Generated | 1 |
| `src/SqlAgent.Storage/ProjectService.cs` | List, create, rename, delete, move | 1 |
| `src/SqlAgent.Storage/ChatService.cs` | `ListHistoryAsync` excludes chats in a project | 1 |
| `tests/SqlAgent.Tests/ProjectServiceTests.cs` | Both delete modes, taken names, moves, counts | 1 |
| `src/SqlAgent.Storage/SearchService.cs` | One `SearchAsync` over chats, messages, projects, databases | 2 |
| `tests/SqlAgent.Tests/SearchServiceTests.cs` | Escaping, caps, per-chat collapse, ordering | 2 |
| `src/SqlAgent.Host/Components/Shared/Chat/NameDialog.razor` | Ask for a name; supersedes `ChatRenameDialog` | 3 |
| `src/SqlAgent.Host/Components/Layout/ChatRow.razor` | One chat row: active state, `⋮` menu | 4 |
| `src/SqlAgent.Host/Components/Layout/HistorySection.razor` | Renders `ChatRow`; loses its own row markup | 4 |
| `src/SqlAgent.Host/Components/Shared/Chat/MoveToProjectDialog.razor` | Pick a project, or none | 5 |
| `src/SqlAgent.Host/Components/Layout/ProjectSection.razor` | Collapsible project list with an add button | 5 |
| `src/SqlAgent.Host/Components/Shared/Ui/Icon.razor` | `folder`, `chevron-right`, `search` | 5 |
| `src/SqlAgent.Host/Web/ShortcutService.cs` | Circuit-scoped `EscapePressed`, `SearchRequested` | 6 |
| `src/SqlAgent.Host/wwwroot/js/shortcuts.js` | One document `keydown` listener | 6 |
| `src/SqlAgent.Host/Components/Layout/KeyboardShortcuts.razor` | Owns the interop; renders nothing | 6 |
| `src/SqlAgent.Host/Components/Shared/Ui/Menu.razor`, `Modal.razor` | Subscribe to `EscapePressed` while open | 6 |
| `src/SqlAgent.Host/Components/Shared/Chat/SearchDialog.razor` | The search modal and its keyboard | 7 |
| `src/SqlAgent.Host/Components/Layout/SidebarNav.razor` | A Search row that opens it | 7 |
| `tests/SqlAgent.Tests/ChatPageTests.cs`, `RestyleRegressionTests.cs`, `UiPrimitiveTests.cs` | Three carried debts | 8 |
| `src/SqlAgent.Host/Components/Pages/ChatPage.razor` | Renamed from `Chat.razor`; routes unchanged | 8 |
| `docs/web-ui.md`, `README.md` | Projects, search, the new manual checks | 9 |

---

### Task 1: Projects in the store

**Files:**
- Create: `src/SqlAgent.Storage/Project.cs`
- Create: `src/SqlAgent.Storage/ProjectService.cs`
- Modify: `src/SqlAgent.Storage/ChatEntities.cs` (add `ProjectId` and the navigation)
- Modify: `src/SqlAgent.Storage/SqlAgentDbContext.cs`
- Modify: `src/SqlAgent.Storage/ChatService.cs` (`ListHistoryAsync`)
- Modify: `src/SqlAgent.Host/Program.cs` (register `ProjectService`)
- Generate: `src/SqlAgent.Storage/Migrations/*_Projects.cs`
- Test: `tests/SqlAgent.Tests/ProjectServiceTests.cs`
- Modify: `tests/SqlAgent.Tests/ChatServiceTests.cs` — **one** existing test changes; see Step 6

**Interfaces:**
- Consumes: `SqlAgentDbContext`, `ChatSummary`, `StoreInitializer` (all from B1).
- Produces:
  - `class Project { Guid Id; string Name; DateTime CreatedAt; DateTime UpdatedAt; List<Chat> Chats; }`
  - `Chat.ProjectId: Guid?` and `Chat.Project: Project?`
  - `record ProjectSummary(Guid Id, string Name, int ChatCount)`
  - `enum ProjectWriteOutcome { Ok, NameTaken, NotFound }`
  - `record ProjectWriteResult(ProjectWriteOutcome Outcome, Guid? Id = null)`
  - `enum ProjectDeleteMode { KeepChats, DeleteChats }`
  - `ProjectService.ListProjectsAsync(CancellationToken ct = default): Task<IReadOnlyList<ProjectSummary>>`
  - `ProjectService.ListChatsInProjectAsync(Guid projectId, CancellationToken ct = default): Task<IReadOnlyList<ChatSummary>>`
  - `ProjectService.CreateProjectAsync(string name, CancellationToken ct = default): Task<ProjectWriteResult>`
  - `ProjectService.RenameProjectAsync(Guid id, string name, CancellationToken ct = default): Task<ProjectWriteResult>`
  - `ProjectService.DeleteProjectAsync(Guid id, ProjectDeleteMode mode, CancellationToken ct = default): Task<bool>`
  - `ProjectService.MoveChatAsync(Guid chatId, Guid? projectId, CancellationToken ct = default): Task<bool>`
  - `ProjectService.NameFrom(string name): string` — trimmed, cut to 60, `"Untitled project"` when blank.

- [ ] **Step 1: Write the failing test**

Create `tests/SqlAgent.Tests/ProjectServiceTests.cs`:

```csharp
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SqlAgent.Storage;

namespace SqlAgent.Tests;

public class ProjectServiceTests : IDisposable
{
    private readonly SqliteConnection _conn = new("DataSource=:memory:");
    private readonly SqlAgentDbContext _db;
    private readonly ProjectService _projects;
    private readonly ChatService _chats;

    public ProjectServiceTests()
    {
        _conn.Open();
        _db = new SqlAgentDbContext(
            new DbContextOptionsBuilder<SqlAgentDbContext>().UseSqlite(_conn).Options);
        _db.Database.EnsureCreated();
        _projects = new ProjectService(_db);
        _chats = new ChatService(_db);
    }

    [Fact]
    public async Task A_project_lists_with_the_number_of_chats_in_it()
    {
        var id = await NewProjectAsync("quarterly");
        await MoveNewChatAsync("first", id);
        await MoveNewChatAsync("second", id);
        await _chats.CreateChatAsync("ungrouped");

        var summary = Assert.Single(await _projects.ListProjectsAsync());

        Assert.Equal("quarterly", summary.Name);
        Assert.Equal(2, summary.ChatCount);
    }

    [Fact]
    public async Task A_name_already_taken_is_reported_rather_than_thrown()
    {
        // The UI needs to say "that name is taken" beside the field. A bool could not tell that apart
        // from "the project is gone", and an exception would surface as the work-area error panel.
        await NewProjectAsync("quarterly");

        var again = await _projects.CreateProjectAsync("quarterly");

        Assert.Equal(ProjectWriteOutcome.NameTaken, again.Outcome);
        Assert.Null(again.Id);
        Assert.Single(await _projects.ListProjectsAsync());
    }

    [Fact]
    public async Task Names_differing_only_in_case_are_the_same_name()
    {
        // Two projects called "Quarterly" and "quarterly" in one sidebar is a papercut with no upside,
        // so the column collates NOCASE and the uniqueness check inherits that.
        await NewProjectAsync("Quarterly");

        var again = await _projects.CreateProjectAsync("quarterly");

        Assert.Equal(ProjectWriteOutcome.NameTaken, again.Outcome);
    }

    [Fact]
    public async Task Renaming_to_a_taken_name_is_refused_and_renaming_a_missing_project_says_so()
    {
        var first = await NewProjectAsync("quarterly");
        await NewProjectAsync("ad hoc");

        Assert.Equal(ProjectWriteOutcome.NameTaken,
            (await _projects.RenameProjectAsync(first, "ad hoc")).Outcome);
        Assert.Equal(ProjectWriteOutcome.NotFound,
            (await _projects.RenameProjectAsync(Guid.NewGuid(), "anything")).Outcome);
        Assert.Equal(ProjectWriteOutcome.Ok,
            (await _projects.RenameProjectAsync(first, "quarterly revenue")).Outcome);
    }

    [Fact]
    public async Task Renaming_a_project_to_its_own_name_is_allowed()
    {
        // Opening the rename dialog and pressing Save without editing must not report the project's own
        // name as taken by itself.
        var id = await NewProjectAsync("quarterly");

        Assert.Equal(ProjectWriteOutcome.Ok, (await _projects.RenameProjectAsync(id, "quarterly")).Outcome);
    }

    [Fact]
    public async Task Deleting_a_project_and_keeping_its_chats_returns_them_to_history()
    {
        var id = await NewProjectAsync("quarterly");
        var chat = await MoveNewChatAsync("kept", id);

        Assert.True(await _projects.DeleteProjectAsync(id, ProjectDeleteMode.KeepChats));

        Assert.Empty(await _projects.ListProjectsAsync());
        Assert.Contains(await _chats.ListHistoryAsync(), c => c.Id == chat);
    }

    [Fact]
    public async Task Deleting_a_project_with_its_chats_takes_them_with_it()
    {
        // The only operation in this phase that can destroy a conversation, which is why the caller has
        // to name the mode rather than getting a default.
        var id = await NewProjectAsync("quarterly");
        var chat = await MoveNewChatAsync("doomed", id);

        Assert.True(await _projects.DeleteProjectAsync(id, ProjectDeleteMode.DeleteChats));

        Assert.Empty(await _projects.ListProjectsAsync());
        Assert.Null(await _chats.GetChatAsync(chat));
        Assert.Empty(await _db.ChatMessages.ToListAsync());
    }

    [Fact]
    public async Task Deleting_a_project_that_is_gone_reports_it_rather_than_throwing()
    {
        Assert.False(await _projects.DeleteProjectAsync(Guid.NewGuid(), ProjectDeleteMode.KeepChats));
    }

    [Fact]
    public async Task A_chat_moves_into_a_project_and_back_out_again()
    {
        var id = await NewProjectAsync("quarterly");
        var chat = await _chats.CreateChatAsync("wandering");

        Assert.True(await _projects.MoveChatAsync(chat, id));
        Assert.Contains(await _projects.ListChatsInProjectAsync(id), c => c.Id == chat);
        Assert.DoesNotContain(await _chats.ListHistoryAsync(), c => c.Id == chat);

        Assert.True(await _projects.MoveChatAsync(chat, null));
        Assert.Empty(await _projects.ListChatsInProjectAsync(id));
        Assert.Contains(await _chats.ListHistoryAsync(), c => c.Id == chat);
    }

    [Fact]
    public async Task Moving_a_chat_that_is_gone_or_into_a_project_that_is_gone_reports_it()
    {
        var chat = await _chats.CreateChatAsync("wandering");

        Assert.False(await _projects.MoveChatAsync(Guid.NewGuid(), null));
        Assert.False(await _projects.MoveChatAsync(chat, Guid.NewGuid()));
        // The failed move left the chat where it was rather than orphaning its project id.
        Assert.Contains(await _chats.ListHistoryAsync(), c => c.Id == chat);
    }

    [Fact]
    public void A_blank_name_gets_a_placeholder_and_a_long_one_is_cut()
    {
        Assert.Equal("Untitled project", ProjectService.NameFrom("   "));
        Assert.Equal("quarterly", ProjectService.NameFrom("  quarterly  "));
        Assert.Equal(60, ProjectService.NameFrom(new string('x', 200)).Length);
    }

    private async Task<Guid> NewProjectAsync(string name)
    {
        var result = await _projects.CreateProjectAsync(name);
        Assert.Equal(ProjectWriteOutcome.Ok, result.Outcome);
        return result.Id!.Value;
    }

    private async Task<Guid> MoveNewChatAsync(string title, Guid projectId)
    {
        var chat = await _chats.CreateChatAsync(title);
        await _chats.AppendMessageAsync(new ChatMessageInput(chat, ChatRole.User, "q", []));
        Assert.True(await _projects.MoveChatAsync(chat, projectId));
        return chat;
    }

    public void Dispose()
    {
        _db.Dispose();
        _conn.Dispose();
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/SqlAgent.Tests/SqlAgent.Tests.csproj --filter ProjectServiceTests`
Expected: FAIL — compile error, `Project`, `ProjectService` and their types do not exist.

- [ ] **Step 3: Write the entity**

Create `src/SqlAgent.Storage/Project.cs`:

```csharp
namespace SqlAgent.Storage;

/// <summary>
/// A folder for chats. A chat belongs to at most one project, and a chat that belongs to one leaves the
/// history list — the sidebar shows each conversation in exactly one place, so "move to project" reads
/// literally rather than adding a second copy somewhere else.
/// </summary>
public class Project
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>Its chats. Present so the chat count is one grouped query rather than one query per
    /// project, and so the delete path can decide their fate explicitly.</summary>
    public List<Chat> Chats { get; set; } = [];
}
```

In `src/SqlAgent.Storage/ChatEntities.cs`, add to `Chat`:

```csharp
    /// <summary>The project this chat lives in, or null for an ungrouped chat — which is what the
    /// history list shows. A chat is never in both places.</summary>
    public Guid? ProjectId { get; set; }
    public Project? Project { get; set; }
```

- [ ] **Step 4: Configure it on the context**

In `src/SqlAgent.Storage/SqlAgentDbContext.cs`, add the set beside the others:

```csharp
    public DbSet<Project> Projects => Set<Project>();
```

and this configuration at the end of `OnModelCreating`:

```csharp
        b.Entity<Project>(e =>
        {
            e.HasKey(x => x.Id);
            // NOCASE so "Quarterly" and "quarterly" are the same name. Two projects differing only in
            // case would be indistinguishable in a sidebar row and useful to nobody; collating the
            // column rather than lower-casing in the query means the unique index enforces it too,
            // instead of the check and the index disagreeing.
            e.Property(x => x.Name).UseCollation("NOCASE").HasMaxLength(60);
            e.HasIndex(x => x.Name).IsUnique();
            // Restrict, not Cascade or SetNull: deleting a project has to be an explicit decision about
            // its chats, and ProjectService.DeleteProjectAsync is where that decision is made. With a
            // cascade, a stray Remove() on a Project would silently take conversations with it.
            e.HasMany(x => x.Chats).WithOne(c => c.Project)
                .HasForeignKey(c => c.ProjectId).OnDelete(DeleteBehavior.Restrict);
        });
```

and inside the existing `b.Entity<Chat>(...)` block, beside its `LastMessageAt` index:

```csharp
            e.HasIndex(x => x.ProjectId);
```

- [ ] **Step 5: Write `ProjectService`**

Create `src/SqlAgent.Storage/ProjectService.cs`:

```csharp
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace SqlAgent.Storage;

/// <summary>A project and how many chats are in it.</summary>
public record ProjectSummary(Guid Id, string Name, int ChatCount);

/// <summary>What a project write did. Not a <c>bool</c>: "that name is taken" and "the project is gone"
/// need different words in the dialog, and a bool cannot tell them apart.</summary>
public enum ProjectWriteOutcome { Ok, NameTaken, NotFound }

public record ProjectWriteResult(ProjectWriteOutcome Outcome, Guid? Id = null);

/// <summary>What to do with a project's chats when the project goes.</summary>
public enum ProjectDeleteMode { KeepChats, DeleteChats }

/// <summary>
/// Projects and which chats are in them. The store only: the dialogs that ask "keep or delete?" live in
/// the sidebar, and this class never decides that on the caller's behalf.
/// </summary>
public class ProjectService(SqlAgentDbContext db)
{
    /// <summary>SQLite's constraint-violation result code, as <see cref="ChatService"/> uses it.</summary>
    private const int SqliteConstraint = 19;

    public async Task<IReadOnlyList<ProjectSummary>> ListProjectsAsync(CancellationToken ct = default) =>
        // p.Chats.Count becomes a correlated subquery, so this is one round trip whatever the project
        // count is — not a query per row.
        await db.Projects
            .OrderBy(p => p.Name)
            .Select(p => new ProjectSummary(p.Id, p.Name, p.Chats.Count))
            .ToListAsync(ct);

    /// <summary>The chats in one project, ordered exactly as the history list orders its own, so the
    /// same row component renders both without either side knowing where it is.</summary>
    public async Task<IReadOnlyList<ChatSummary>> ListChatsInProjectAsync(
        Guid projectId, CancellationToken ct = default) =>
        await db.Chats
            .Where(c => c.ProjectId == projectId)
            .OrderByDescending(c => c.LastMessageAt).ThenByDescending(c => c.CreatedAt)
            .Select(c => new ChatSummary(c.Id, c.Title, c.LastMessageAt))
            .ToListAsync(ct);

    public async Task<ProjectWriteResult> CreateProjectAsync(string name, CancellationToken ct = default)
    {
        var trimmed = NameFrom(name);
        // Read first so the dialog can say "that name is taken" plainly; the catch below is the backstop
        // that makes the race between this read and the write harmless. Same two-layer shape
        // ChatService uses for message sequence numbers.
        if (await db.Projects.AnyAsync(p => p.Name == trimmed, ct))
            return new ProjectWriteResult(ProjectWriteOutcome.NameTaken);

        var now = DateTime.UtcNow;
        var project = new Project { Id = Guid.NewGuid(), Name = trimmed, CreatedAt = now, UpdatedAt = now };
        db.Projects.Add(project);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsNameCollision(ex))
        {
            db.Entry(project).State = EntityState.Detached;
            return new ProjectWriteResult(ProjectWriteOutcome.NameTaken);
        }
        return new ProjectWriteResult(ProjectWriteOutcome.Ok, project.Id);
    }

    public async Task<ProjectWriteResult> RenameProjectAsync(
        Guid id, string name, CancellationToken ct = default)
    {
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (project is null) return new ProjectWriteResult(ProjectWriteOutcome.NotFound);

        var trimmed = NameFrom(name);
        // Excluding this project from the check: pressing Save without editing must not report a
        // project's own name as taken by itself.
        if (await db.Projects.AnyAsync(p => p.Id != id && p.Name == trimmed, ct))
            return new ProjectWriteResult(ProjectWriteOutcome.NameTaken);

        project.Name = trimmed;
        project.UpdatedAt = DateTime.UtcNow;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsNameCollision(ex))
        {
            await db.Entry(project).ReloadAsync(ct);
            return new ProjectWriteResult(ProjectWriteOutcome.NameTaken);
        }
        return new ProjectWriteResult(ProjectWriteOutcome.Ok, id);
    }

    public async Task<bool> DeleteProjectAsync(
        Guid id, ProjectDeleteMode mode, CancellationToken ct = default)
    {
        var project = await db.Projects
            .Include(p => p.Chats)
            .FirstOrDefaultAsync(p => p.Id == id, ct);
        if (project is null) return false;

        // The chats have to be dealt with first: the foreign key is Restrict, so removing the project
        // while anything still points at it fails rather than deciding for the user.
        if (mode == ProjectDeleteMode.DeleteChats)
            db.Chats.RemoveRange(project.Chats);
        else
            foreach (var chat in project.Chats) chat.ProjectId = null;

        db.Projects.Remove(project);
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Moves a chat into a project, or out of every project when <paramref name="projectId"/>
    /// is null — which is what "remove from project" and "move to another" both are.</summary>
    public async Task<bool> MoveChatAsync(Guid chatId, Guid? projectId, CancellationToken ct = default)
    {
        var chat = await db.Chats.FirstOrDefaultAsync(c => c.Id == chatId, ct);
        if (chat is null) return false;

        // Checked rather than left to the foreign key: a missing project should read as "no" to the
        // caller, not as a DbUpdateException surfacing through the work-area error panel.
        if (projectId is { } target && !await db.Projects.AnyAsync(p => p.Id == target, ct))
            return false;

        chat.ProjectId = projectId;
        chat.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>A project's display name: trimmed, cut to what the sidebar row fits, never blank.</summary>
    public static string NameFrom(string name)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0) return "Untitled project";
        return trimmed.Length <= 60 ? trimmed : trimmed[..60];
    }

    private static bool IsNameCollision(DbUpdateException ex) =>
        ex.InnerException is SqliteException { SqliteErrorCode: SqliteConstraint } sqlite
        && sqlite.Message.Contains("Projects.Name", StringComparison.Ordinal);
}
```

- [ ] **Step 6: Take projected chats out of the history list**

In `src/SqlAgent.Storage/ChatService.cs`, `ListHistoryAsync` gains one clause:

```csharp
    public async Task<IReadOnlyList<ChatSummary>> ListHistoryAsync(
        int take = 200, CancellationToken ct = default) =>
        await db.Chats
            // Chats in a project are listed under that project instead. Without this filter the sidebar
            // shows the same conversation twice, in two sections, with two ⋮ menus acting on one row.
            .Where(c => c.ProjectId == null)
            // ThenByDescending(CreatedAt) for the same reason Sequence exists on messages: two chats
            // created or touched inside the same millisecond would otherwise reload in arbitrary order.
            .OrderByDescending(c => c.LastMessageAt).ThenByDescending(c => c.CreatedAt)
            .Take(take)
            .Select(c => new ChatSummary(c.Id, c.Title, c.LastMessageAt))
            .ToListAsync(ct);
```

Then add one fact to `tests/SqlAgent.Tests/ChatServiceTests.cs` — this is the only existing test file
this task touches, and it gains a test rather than changing one:

```csharp
    [Fact]
    public async Task A_chat_in_a_project_is_not_in_the_history_list()
    {
        // The sidebar shows each conversation in exactly one place. This is the store half of that rule;
        // ProjectServiceTests covers the round trip back out.
        var chat = await _chats.CreateChatAsync("grouped");
        var project = new Project
        {
            Id = Guid.NewGuid(), Name = "quarterly",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        _db.Projects.Add(project);
        await _db.SaveChangesAsync();
        (await _db.Chats.FirstAsync(c => c.Id == chat)).ProjectId = project.Id;
        await _db.SaveChangesAsync();

        Assert.DoesNotContain(await _chats.ListHistoryAsync(), c => c.Id == chat);
    }
```

- [ ] **Step 7: Generate the migration**

```bash
dotnet ef migrations add Projects --project src/SqlAgent.Storage
```

Verify it adds one table and one column and nothing else:

```bash
grep -E 'CreateTable|DropTable|AddColumn|DropColumn' src/SqlAgent.Storage/Migrations/*_Projects.cs
```

Expected: one `CreateTable` for `Projects` and one `AddColumn` for `Chats.ProjectId`. Anything else means
the model drifted; stop and reconcile rather than editing the generated file.

- [ ] **Step 8: Register the service**

In `src/SqlAgent.Host/Program.cs`, beside `ChatService`:

```csharp
builder.Services.AddScoped<ProjectService>();
```

- [ ] **Step 9: Run the tests to verify they pass**

Run: `dotnet test tests/SqlAgent.Tests/SqlAgent.Tests.csproj --filter "ProjectServiceTests|ChatServiceTests|StoreMigrationTests"`
Expected: PASS. `StoreMigrationTests` is in the filter deliberately: it asserts a rescued legacy store
ends up schema-identical to one migrated from nothing, so a new migration is exactly when that guard
earns its place.

- [ ] **Step 10: Run the whole suite**

Run: `dotnet test SqlAgent.slnx --configuration Release`
Expected: PASS.

- [ ] **Step 11: Commit**

```bash
git add src/SqlAgent.Storage src/SqlAgent.Host/Program.cs tests/SqlAgent.Tests
git commit -m "$(cat <<'COMMIT'
Add projects, and take chats in one out of the history list

A chat belongs to at most one project, and a chat that belongs to one leaves the
history list — the sidebar shows each conversation in exactly one place, so
"move to project" reads literally instead of adding a second copy somewhere else.

The foreign key is Restrict rather than Cascade or SetNull: deleting a project
has to be an explicit decision about its chats, and DeleteProjectAsync is where
that decision is made. A cascade would let a stray Remove() take conversations
with it. Names collate NOCASE, so "Quarterly" and "quarterly" are one name in
both the uniqueness check and the index rather than disagreeing.

Writes return an outcome instead of a bool, because "that name is taken" and
"the project is gone" need different words in the dialog.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
COMMIT
)"
```

---

### Task 2: `SearchService`

**Files:**
- Create: `src/SqlAgent.Storage/SearchService.cs`
- Modify: `src/SqlAgent.Host/Program.cs` (register it)
- Test: `tests/SqlAgent.Tests/SearchServiceTests.cs`

**Interfaces:**
- Consumes: `SqlAgentDbContext`, and `Project` from Task 1.
- Produces:
  - `enum SearchHitKind { Chat, Message, Project, Database }`
  - `record SearchHit(SearchHitKind Kind, Guid TargetId, string Label, string? Snippet)`
  - `SearchService.SearchAsync(string term, CancellationToken ct = default): Task<IReadOnlyList<SearchHit>>`

- [ ] **Step 1: Write the failing test**

Create `tests/SqlAgent.Tests/SearchServiceTests.cs`:

```csharp
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SqlAgent.Core;
using SqlAgent.Storage;

namespace SqlAgent.Tests;

/// <summary>
/// The pattern-escaping cases are the reason this class exists as its own file. A search for "50%" that
/// silently matches every chat, or "_" that matches every single character, is the kind of defect a user
/// cannot diagnose and would not report as a bug — it just makes search feel broken.
/// </summary>
public class SearchServiceTests : IDisposable
{
    private readonly SqliteConnection _conn = new("DataSource=:memory:");
    private readonly SqlAgentDbContext _db;
    private readonly SearchService _search;
    private readonly ChatService _chats;

    public SearchServiceTests()
    {
        _conn.Open();
        _db = new SqlAgentDbContext(
            new DbContextOptionsBuilder<SqlAgentDbContext>().UseSqlite(_conn).Options);
        _db.Database.EnsureCreated();
        _search = new SearchService(_db);
        _chats = new ChatService(_db);
    }

    [Fact]
    public async Task A_blank_term_returns_nothing_and_asks_the_database_nothing()
    {
        await ChatWithMessageAsync("quarterly revenue", "anything at all");

        Assert.Empty(await _search.SearchAsync(""));
        Assert.Empty(await _search.SearchAsync("   "));
    }

    [Fact]
    public async Task A_chat_is_found_by_its_title()
    {
        var id = await ChatWithMessageAsync("quarterly revenue", "unrelated body");

        var hit = Assert.Single(await _search.SearchAsync("quarterly"));

        Assert.Equal(SearchHitKind.Chat, hit.Kind);
        Assert.Equal(id, hit.TargetId);
        Assert.Equal("quarterly revenue", hit.Label);
        Assert.Null(hit.Snippet);
    }

    [Fact]
    public async Task A_chat_is_found_by_its_message_text_with_a_snippet_around_the_match()
    {
        var id = await ChatWithMessageAsync(
            "untitled", "the quick brown fox jumps over the lazy dog and keeps going for a while");

        var hit = Assert.Single(await _search.SearchAsync("lazy"));

        Assert.Equal(SearchHitKind.Message, hit.Kind);
        // The chat is what opens, so the chat's id is what the hit carries — not the message's.
        Assert.Equal(id, hit.TargetId);
        Assert.Contains("lazy", hit.Snippet);
        // Cut, not the whole message: a long answer would otherwise fill the result list.
        Assert.True(hit.Snippet!.Length < 100);
    }

    [Fact]
    public async Task A_chat_whose_messages_match_many_times_appears_once()
    {
        // Without this, one talkative conversation pushes every other result off the list.
        var id = await _chats.CreateChatAsync("untitled");
        for (var i = 0; i < 5; i++)
            await _chats.AppendMessageAsync(new ChatMessageInput(id, ChatRole.User, "lazy again", []));

        var hits = await _search.SearchAsync("lazy");

        Assert.Single(hits, h => h.Kind == SearchHitKind.Message && h.TargetId == id);
    }

    [Fact]
    public async Task A_chat_matching_by_title_and_by_text_is_reported_under_both_kinds()
    {
        // The modal groups by kind, so the same conversation legitimately appears in two groups; what it
        // must not do is appear twice within one group.
        var id = await ChatWithMessageAsync("lazy plans", "the lazy dog");

        var hits = await _search.SearchAsync("lazy");

        Assert.Single(hits, h => h.Kind == SearchHitKind.Chat && h.TargetId == id);
        Assert.Single(hits, h => h.Kind == SearchHitKind.Message && h.TargetId == id);
    }

    [Theory]
    // Each wildcard gets its own case because each breaks differently and all three break quietly.
    [InlineData("50%")]
    [InlineData("a_b")]
    [InlineData("back\\slash")]
    public async Task A_wildcard_in_the_term_is_matched_literally(string literal)
    {
        await ChatWithMessageAsync($"about {literal} exactly", "unrelated");
        await ChatWithMessageAsync("nothing like it", "unrelated");

        var hits = await _search.SearchAsync(literal);

        var hit = Assert.Single(hits, h => h.Kind == SearchHitKind.Chat);
        Assert.Contains(literal, hit.Label);
    }

    [Fact]
    public async Task An_underscore_does_not_match_an_arbitrary_character()
    {
        // The sharpest form of the same bug: "a_b" must not find "axb".
        await ChatWithMessageAsync("axb", "unrelated");

        Assert.Empty(await _search.SearchAsync("a_b"));
    }

    [Fact]
    public async Task Projects_and_databases_are_found_by_name()
    {
        var projects = new ProjectService(_db);
        var created = await projects.CreateProjectAsync("quarterly work");
        var connections = new DatabaseConnectionService(_db, new InMemorySecretStore());
        var connection = await connections.CreateAsync(
            new DatabaseConnectionInput("quarterly reporting", DatabaseProviderType.Postgres, true), "cs");

        var hits = await _search.SearchAsync("quarterly");

        var project = Assert.Single(hits, h => h.Kind == SearchHitKind.Project);
        Assert.Equal(created.Id, project.TargetId);
        var database = Assert.Single(hits, h => h.Kind == SearchHitKind.Database);
        Assert.Equal(connection.Id, database.TargetId);
    }

    [Fact]
    public async Task Hits_of_one_kind_come_back_newest_first()
    {
        var older = await ChatWithMessageAsync("lazy one", "x");
        var newer = await ChatWithMessageAsync("lazy two", "x");
        // AppendMessageAsync moves LastMessageAt, which is the order the sidebar uses everywhere else.
        await _chats.AppendMessageAsync(new ChatMessageInput(older, ChatRole.User, "later", []));

        var chats = (await _search.SearchAsync("lazy"))
            .Where(h => h.Kind == SearchHitKind.Chat).Select(h => h.TargetId).ToList();

        Assert.Equal([older, newer], chats);
    }

    [Fact]
    public async Task No_kind_returns_more_than_fifty_hits()
    {
        for (var i = 0; i < 55; i++) await ChatWithMessageAsync($"lazy {i}", "x");

        var hits = await _search.SearchAsync("lazy");

        Assert.Equal(50, hits.Count(h => h.Kind == SearchHitKind.Chat));
    }

    private async Task<Guid> ChatWithMessageAsync(string title, string body)
    {
        var id = await _chats.CreateChatAsync(title);
        await _chats.AppendMessageAsync(new ChatMessageInput(id, ChatRole.User, body, []));
        return id;
    }

    public void Dispose()
    {
        _db.Dispose();
        _conn.Dispose();
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/SqlAgent.Tests/SqlAgent.Tests.csproj --filter SearchServiceTests`
Expected: FAIL — compile error, `SearchService` does not exist.

- [ ] **Step 3: Write `SearchService`**

Create `src/SqlAgent.Storage/SearchService.cs`:

```csharp
using Microsoft.EntityFrameworkCore;

namespace SqlAgent.Storage;

/// <summary>What a hit points at. The modal groups its results by this.</summary>
public enum SearchHitKind { Chat, Message, Project, Database }

/// <summary>
/// One result. <see cref="TargetId"/> is what opening it navigates to — for a message hit that is the
/// chat's id, not the message's, because the chat is what opens. <see cref="Snippet"/> is set only for
/// message hits, where the label alone would not explain why the chat matched.
/// </summary>
public record SearchHit(SearchHitKind Kind, Guid TargetId, string Label, string? Snippet);

/// <summary>
/// One query box over chats, their messages, projects and database connections.
///
/// Deliberately LIKE and not an FTS table: the corpus is one person's history on a local SQLite file,
/// and an FTS index would be a second copy of every message to keep in step for a search that already
/// returns in milliseconds. The cost of that choice is that there is no relevance to rank by, so hits
/// come back newest-first within their kind, which is honest about what the query knows.
/// </summary>
public class SearchService(SqlAgentDbContext db)
{
    /// <summary>Per kind, so one very common word cannot bury the other kinds.</summary>
    private const int PerKindCap = 50;

    /// <summary>Characters either side of the match in a message snippet.</summary>
    private const int SnippetContext = 40;

    /// <summary>The LIKE escape character, passed explicitly to EF's three-argument overload. SQLite has
    /// no default escape character at all — without this, an escaped pattern would match the backslashes
    /// literally.</summary>
    private const string Escape = "\\";

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(string term, CancellationToken ct = default)
    {
        // A blank box is not a query for everything. Returning early also keeps the modal from issuing a
        // full scan on the keystroke that clears the field.
        if (string.IsNullOrWhiteSpace(term)) return [];

        var trimmed = term.Trim();
        var pattern = $"%{EscapeForLike(trimmed)}%";
        var hits = new List<SearchHit>();

        hits.AddRange(await db.Chats
            .Where(c => EF.Functions.Like(c.Title, pattern, Escape))
            .OrderByDescending(c => c.LastMessageAt).ThenByDescending(c => c.CreatedAt)
            .Take(PerKindCap)
            .Select(c => new SearchHit(SearchHitKind.Chat, c.Id, c.Title, null))
            .ToListAsync(ct));

        // One row per chat, not per matching message: the correlated subquery picks the earliest match
        // in each conversation, so a chat that says the word fifteen times still takes one slot.
        var byText = await db.Chats
            .Where(c => c.Messages.Any(m => EF.Functions.Like(m.Text, pattern, Escape)))
            .OrderByDescending(c => c.LastMessageAt).ThenByDescending(c => c.CreatedAt)
            .Take(PerKindCap)
            .Select(c => new
            {
                c.Id,
                c.Title,
                Match = c.Messages
                    .Where(m => EF.Functions.Like(m.Text, pattern, Escape))
                    .OrderBy(m => m.Sequence)
                    .Select(m => m.Text)
                    .FirstOrDefault(),
            })
            .ToListAsync(ct);

        hits.AddRange(byText.Select(c =>
            new SearchHit(SearchHitKind.Message, c.Id, c.Title, Snippet(c.Match ?? "", trimmed))));

        hits.AddRange(await db.Projects
            .Where(p => EF.Functions.Like(p.Name, pattern, Escape))
            .OrderBy(p => p.Name)
            .Take(PerKindCap)
            .Select(p => new SearchHit(SearchHitKind.Project, p.Id, p.Name, null))
            .ToListAsync(ct));

        hits.AddRange(await db.DatabaseConnections
            .Where(d => EF.Functions.Like(d.Name, pattern, Escape))
            .OrderBy(d => d.Name)
            .Take(PerKindCap)
            .Select(d => new SearchHit(SearchHitKind.Database, d.Id, d.Name, null))
            .ToListAsync(ct));

        return hits;
    }

    /// <summary>
    /// Makes a user's text safe to drop inside a LIKE pattern. The escape character goes first: doing it
    /// last would escape the backslashes this method just added. Without this, "50%" matches every row
    /// and "a_b" matches "axb" — quietly, and with nothing for the user to diagnose.
    /// </summary>
    private static string EscapeForLike(string term) => term
        .Replace("\\", "\\\\")
        .Replace("%", "\\%")
        .Replace("_", "\\_");

    /// <summary>
    /// A window around the first occurrence, with ellipses where text was cut. Done here rather than in
    /// SQL because SQLite has no expression for it, and the row is already in memory by this point.
    /// The comparison is case-insensitive to match SQLite's own LIKE; if it still cannot be found — a
    /// non-ASCII case fold the two disagree about — the opening of the message is a better answer than
    /// nothing.
    /// </summary>
    private static string Snippet(string text, string term)
    {
        var at = text.IndexOf(term, StringComparison.OrdinalIgnoreCase);
        if (at < 0) return text.Length <= SnippetContext * 2 ? text : text[..(SnippetContext * 2)] + "…";

        var start = Math.Max(0, at - SnippetContext);
        var end = Math.Min(text.Length, at + term.Length + SnippetContext);
        return (start > 0 ? "…" : "") + text[start..end] + (end < text.Length ? "…" : "");
    }
}
```

- [ ] **Step 4: Register the service**

In `src/SqlAgent.Host/Program.cs`, beside `ProjectService`:

```csharp
builder.Services.AddScoped<SearchService>();
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/SqlAgent.Tests/SqlAgent.Tests.csproj --filter SearchServiceTests`
Expected: PASS, 12 tests (9 facts plus 3 theory cases).

If `A_wildcard_in_the_term_is_matched_literally` fails only for the backslash case, the escape ordering
in `EscapeForLike` is wrong — the escape character must be doubled before the wildcards are escaped, or
the backslashes added for `%` and `_` get escaped a second time.

- [ ] **Step 6: Run the whole suite**

Run: `dotnet test SqlAgent.slnx --configuration Release`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add src/SqlAgent.Storage/SearchService.cs src/SqlAgent.Host/Program.cs tests/SqlAgent.Tests/SearchServiceTests.cs
git commit -m "$(cat <<'COMMIT'
Search chats, messages, projects and databases from one query

LIKE rather than an FTS table: the corpus is one person's history on a local
SQLite file, and an index would be a second copy of every message to keep in step
for a search that already returns in milliseconds. The cost is that there is
nothing to rank by, so hits come back newest-first within their kind.

The escaping is the part that matters. SQLite has no default LIKE escape
character, so the pattern passes one explicitly and the term has %, _ and the
escape character itself replaced before it goes in — the escape character first,
or it would escape the backslashes the other two just added. Without that, "50%"
matches every row and "a_b" matches "axb", quietly, with nothing for the user to
diagnose.

Message hits collapse to one per chat so a conversation that says the word
fifteen times cannot push every other result off the list.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
COMMIT
)"
```

---

### Task 3: `NameDialog` replaces `ChatRenameDialog`

**Files:**
- Create: `src/SqlAgent.Host/Components/Shared/Chat/NameDialog.razor`
- Delete: `src/SqlAgent.Host/Components/Shared/Chat/ChatRenameDialog.razor`
- Modify: `src/SqlAgent.Host/Components/Layout/HistorySection.razor` (its rename call)
- Modify: `tests/SqlAgent.Tests/HistorySectionTests.cs` — two existing tests change; see Step 4

**Interfaces:**
- Consumes: `Modal` from Phase A.
- Produces: `<NameDialog Title="string" Label="string" InitialValue="string" ConfirmLabel="string" OnSave="EventCallback<string>" OnCancel="EventCallback" />`, with `data-testid="name-save"` and `data-testid="name-cancel"` on its footer buttons.

Phase B2 asks for a name in three places: renaming a chat, creating a project, renaming a project.
`ChatRenameDialog` is the first of those wearing a specific name. This task generalizes it; Task 5 is
what gives it its other two callers.

**Not in this task:** the "that name is taken" message. Nothing can produce it yet — a chat rename has no
uniqueness rule — and this project does not ship a parameter nothing renders. Task 5 adds it with the
project dialogs that need it.

- [ ] **Step 1: Write the failing test**

Create `tests/SqlAgent.Tests/NameDialogTests.cs`:

```csharp
using Bunit;
using Microsoft.AspNetCore.Components;
using SqlAgent.Host.Components.Shared.Chat;

namespace SqlAgent.Tests;

public class NameDialogTests
{
    [Fact]
    public void It_opens_with_the_current_name_already_in_the_box()
    {
        // Renaming starts from what the thing is called, so the common edit — fixing one word — does not
        // begin by retyping the whole name.
        using var ctx = new Bunit.TestContext();

        var dialog = ctx.RenderComponent<NameDialog>(p => p
            .Add(d => d.Title, "Rename chat")
            .Add(d => d.Label, "Title")
            .Add(d => d.InitialValue, "quarterly revenue"));

        Assert.Equal("quarterly revenue", dialog.Find("input").GetAttribute("value"));
    }

    [Fact]
    public void Saving_reports_the_edited_name()
    {
        using var ctx = new Bunit.TestContext();
        var saved = "";

        var dialog = ctx.RenderComponent<NameDialog>(p => p
            .Add(d => d.Title, "Rename chat")
            .Add(d => d.InitialValue, "old")
            .Add(d => d.OnSave, EventCallback.Factory.Create<string>(new object(), v => saved = v)));
        dialog.Find("input").Change("new");
        dialog.Find("[data-testid=name-save]").Click();

        Assert.Equal("new", saved);
    }

    [Fact]
    public void An_empty_name_cannot_be_saved()
    {
        // The services substitute a placeholder for a blank name, but a dialog that accepts one and then
        // shows something the user did not type reads as a bug rather than as a default.
        using var ctx = new Bunit.TestContext();

        var dialog = ctx.RenderComponent<NameDialog>(p => p
            .Add(d => d.Title, "New project")
            .Add(d => d.InitialValue, ""));

        Assert.True(dialog.Find("[data-testid=name-save]").HasAttribute("disabled"));

        dialog.Find("input").Change("   ");
        Assert.True(dialog.Find("[data-testid=name-save]").HasAttribute("disabled"));

        dialog.Find("input").Change("quarterly");
        Assert.False(dialog.Find("[data-testid=name-save]").HasAttribute("disabled"));
    }

    [Fact]
    public void Cancelling_reports_nothing()
    {
        using var ctx = new Bunit.TestContext();
        var saves = 0;
        var cancels = 0;

        var dialog = ctx.RenderComponent<NameDialog>(p => p
            .Add(d => d.Title, "Rename chat")
            .Add(d => d.InitialValue, "old")
            .Add(d => d.OnSave, EventCallback.Factory.Create<string>(new object(), _ => saves++))
            .Add(d => d.OnCancel, EventCallback.Factory.Create(new object(), () => cancels++)));

        dialog.Find("[data-testid=name-cancel]").Click();

        Assert.Equal(0, saves);
        Assert.Equal(1, cancels);
    }

    [Fact]
    public void The_confirm_button_can_be_labelled_for_what_it_does()
    {
        // "Save" is right for a rename and wrong for a creation. One dialog, two verbs.
        using var ctx = new Bunit.TestContext();

        var dialog = ctx.RenderComponent<NameDialog>(p => p
            .Add(d => d.Title, "New project")
            .Add(d => d.ConfirmLabel, "Create")
            .Add(d => d.InitialValue, "quarterly"));

        Assert.Equal("Create", dialog.Find("[data-testid=name-save]").TextContent.Trim());
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/SqlAgent.Tests/SqlAgent.Tests.csproj --filter NameDialogTests`
Expected: FAIL — compile error, `NameDialog` does not exist.

- [ ] **Step 3: Write `NameDialog` and delete `ChatRenameDialog`**

Create `src/SqlAgent.Host/Components/Shared/Chat/NameDialog.razor`:

```razor
@* Presentational: it collects a name and reports it. The write happens in whichever component owns the
   service scope — a dialog that saves as a side effect of being rendered is much harder to reason about
   from the caller, and this one is shared by three callers with three different writes. *@
<Modal Title="@Title" OnClose="OnCancel">
    <ChildContent>
        <label for="name-dialog-value">@Label</label>
        @* OnValueChanged is a method rather than an inline lambda: a lambda with a string literal inside
           a Razor attribute needs escaped quotes, which the parser handles badly — SchemaRail's filter
           input carries the same note for the same reason. *@
        <input id="name-dialog-value" value="@_value" @onchange="OnValueChanged" />
    </ChildContent>
    <Footer>
        <button type="button" data-testid="name-cancel" @onclick="OnCancel">Cancel</button>
        <button type="button" class="primary" data-testid="name-save"
                disabled="@string.IsNullOrWhiteSpace(_value)"
                @onclick="() => OnSave.InvokeAsync(_value)">@ConfirmLabel</button>
    </Footer>
</Modal>

@code {
    [Parameter, EditorRequired] public string Title { get; set; } = "";

    /// <summary>The field's label. Defaults to the common case; a project dialog says "Name".</summary>
    [Parameter] public string Label { get; set; } = "Name";

    /// <summary>What the box starts with — the current name when renaming, empty when creating.</summary>
    [Parameter] public string InitialValue { get; set; } = "";

    /// <summary>"Save" for a rename, "Create" for a creation. The verb is the caller's, not the
    /// dialog's.</summary>
    [Parameter] public string ConfirmLabel { get; set; } = "Save";

    [Parameter] public EventCallback<string> OnSave { get; set; }
    [Parameter] public EventCallback OnCancel { get; set; }

    private string _value = "";

    // OnInitialized, not a field initializer: parameters are not set yet when fields initialize.
    protected override void OnInitialized() => _value = InitialValue;

    private void OnValueChanged(ChangeEventArgs e) => _value = e.Value?.ToString() ?? string.Empty;
}
```

Then delete `src/SqlAgent.Host/Components/Shared/Chat/ChatRenameDialog.razor`.

- [ ] **Step 4: Point `HistorySection` at it, and follow the two tests**

In `src/SqlAgent.Host/Components/Layout/HistorySection.razor`, replace the rename dialog call:

```razor
    private void ShowRename(ChatSummary chat) => Dialogs.Show(
        @<NameDialog Title="Rename chat" Label="Title" InitialValue="@chat.Title"
                     OnSave="title => RenameAsync(chat, title)"
                     OnCancel="Dialogs.Close" />);
```

Two tests in `tests/SqlAgent.Tests/HistorySectionTests.cs` name the old dialog's test ids and must
follow it — `Renaming_from_the_row_menu_goes_through_a_dialog_and_updates_the_store` and
`Cancelling_the_rename_dialog_keeps_the_title`. Change `[data-testid=rename-save]` to
`[data-testid=name-save]` and `[data-testid=rename-cancel]` to `[data-testid=name-cancel]`. Change
nothing else in that file: the assertions about what the store ends up holding are exactly what must
keep passing across this refactor.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/SqlAgent.Tests/SqlAgent.Tests.csproj --filter "NameDialogTests|HistorySectionTests"`
Expected: PASS.

- [ ] **Step 6: Run the whole suite**

Run: `dotnet test SqlAgent.slnx --configuration Release`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add src/SqlAgent.Host/Components/Shared/Chat tests/SqlAgent.Tests
git commit -m "$(cat <<'COMMIT'
Generalize the rename dialog into NameDialog

Phase B2 asks for a name in three places — renaming a chat, creating a project,
renaming a project — and ChatRenameDialog was the first of them wearing a
specific name. The generalization is a rename plus two parameters: the field's
label and the confirm button's verb, because "Save" is right for a rename and
wrong for a creation.

No "that name is taken" message yet: nothing can produce one until projects
arrive, and a parameter nothing renders is exactly what this project keeps out.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
COMMIT
)"
```

---

### Task 4: `ChatRow` comes out of `HistorySection`, and owns a chat's actions

**Files:**
- Create: `src/SqlAgent.Host/Components/Layout/ChatRow.razor` + `.razor.css`
- Modify: `src/SqlAgent.Host/Components/Layout/HistorySection.razor` + `.razor.css`
- Test: `tests/SqlAgent.Tests/ChatRowTests.cs`
- Modify: `tests/SqlAgent.Tests/HistorySectionTests.cs` — four tests move or change; see Step 4

**Interfaces:**
- Consumes: `ChatSummary`, `ChatService`, `ScopedRunner`, `AppState`, `DialogService`, `NameDialog` (Task 3), `Menu`, `MenuItem`, `Icon`.
- Produces: `<ChatRow Chat="ChatSummary" Active="bool" />`, rendering `div.chat-row` with `chat-row.active` when selected.

The project section renders the same row. Copying its markup **and its rename/delete handlers** into a
second section would leave roughly thirty lines duplicated in two files — the "verbatim duplication of a
logic block" a reviewer is required to raise, and the defect shape this project has already paid for
twice, once in the collapsed sidebar's two rule sets and once in the two dark palettes.

So the row is not merely markup: it owns what a chat's `⋮` menu does. It resolves its own services, opens
its own dialogs, performs the write, and then calls `AppState.NotifyChatsChanged()` — which both sections
already subscribe to, so each reloads itself without the row knowing either of them exists.

**`Active` stays a parameter, not a read of `AppState` inside the row.** A child that reads circuit state
directly can be skipped by the renderer's diff when its own parameters have not changed, which would
leave a stale highlight on the chat the user just left. Passing it down makes the change visible to the
diff. The row reads `AppState` for *writes* (clearing the selection after deleting the open chat), which
is a different thing from rendering off it.

**Not in this task:** the Move to project item. Its dialog and its target do not exist until Task 5,
which adds the menu item, the dialog and the handler together.

- [ ] **Step 1: Write the failing test**

Create `tests/SqlAgent.Tests/ChatRowTests.cs`:

```csharp
using Bunit;
using Bunit.TestDoubles;
using Microsoft.AspNetCore.Components;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SqlAgent.Host.Components.Layout;
using SqlAgent.Host.Web;
using SqlAgent.Storage;

namespace SqlAgent.Tests;

/// <summary>
/// The row owns a chat's actions rather than reporting them upward, because two sections render it and
/// the alternative is the same thirty lines of dialog-and-write logic in both. These tests therefore
/// assert against the store, not against callbacks.
/// </summary>
public class ChatRowTests : IDisposable
{
    private readonly SqliteConnection _conn = new("DataSource=:memory:");
    private readonly Bunit.TestContext _ctx = new();

    public ChatRowTests()
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
    public async Task The_row_shows_the_title_and_opens_the_chat()
    {
        var chat = await SeedAsync("quarterly revenue");
        var row = _ctx.RenderComponent<ChatRow>(p => p.Add(r => r.Chat, chat));

        row.Find(".chat-row-open").Click();

        var nav = _ctx.Services.GetRequiredService<FakeNavigationManager>();
        Assert.Contains("quarterly revenue", row.Markup);
        Assert.EndsWith($"/chat/{chat.Id}", nav.Uri);
    }

    [Fact]
    public async Task The_active_row_says_so_in_its_class()
    {
        // The sidebar is the only thing telling the user which conversation they are in once the
        // transcript scrolls past the first message.
        var chat = await SeedAsync("quarterly revenue");

        var row = _ctx.RenderComponent<ChatRow>(p => p.Add(r => r.Chat, chat).Add(r => r.Active, true));

        Assert.Contains("active", row.Find(".chat-row").ClassName);
    }

    [Fact]
    public async Task Re_rendering_with_a_new_active_flag_moves_the_highlight()
    {
        // Active is a parameter rather than a read of AppState inside the row precisely so this works:
        // a child that reads circuit state directly can be skipped by the diff when its own parameters
        // have not changed, stranding the highlight on the chat the user just left.
        var chat = await SeedAsync("quarterly revenue");
        var row = _ctx.RenderComponent<ChatRow>(p => p.Add(r => r.Chat, chat).Add(r => r.Active, true));

        row.SetParametersAndRender(p => p.Add(r => r.Active, false));

        Assert.DoesNotContain("active", row.Find(".chat-row").ClassName);
    }

    [Fact]
    public async Task Renaming_goes_through_a_dialog_and_updates_the_store()
    {
        var chat = await SeedAsync("first question, truncated");
        var dialogs = _ctx.Services.GetRequiredService<DialogService>();
        var row = _ctx.RenderComponent<ChatRow>(p => p.Add(r => r.Chat, chat));

        row.Find(".menu-trigger").Click();
        row.FindAll(".menu-item-action").First(r => r.TextContent.Contains("Rename")).Click();

        // Handed to DialogService rather than rendered here: inside the drawer a Modal would resolve its
        // position against the sidebar's transform and ride off-screen with it.
        Assert.NotNull(dialogs.Current);
        var dialog = _ctx.Render(dialogs.Current!);
        dialog.Find("input").Change("quarterly revenue");
        await dialog.Find("[data-testid=name-save]").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        Assert.Equal("quarterly revenue", (await LoadAsync(chat.Id))!.Title);
        Assert.Null(dialogs.Current);
    }

    [Fact]
    public async Task Deleting_asks_first_naming_the_chat_and_then_removes_it()
    {
        var chat = await SeedAsync("throwaway");
        var dialogs = _ctx.Services.GetRequiredService<DialogService>();
        var row = _ctx.RenderComponent<ChatRow>(p => p.Add(r => r.Chat, chat));

        row.Find(".menu-trigger").Click();
        row.FindAll(".menu-item-action").First(r => r.TextContent.Contains("Delete")).Click();

        var dialog = _ctx.Render(dialogs.Current!);
        // "Are you sure?" with no subject is how the wrong one goes.
        Assert.Contains("throwaway", dialog.Markup);
        await dialog.Find("[data-testid=delete-confirm]").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        Assert.Null(await LoadAsync(chat.Id));
    }

    [Fact]
    public async Task Cancelling_the_delete_dialog_keeps_the_chat()
    {
        var chat = await SeedAsync("keep me");
        var dialogs = _ctx.Services.GetRequiredService<DialogService>();
        var row = _ctx.RenderComponent<ChatRow>(p => p.Add(r => r.Chat, chat));
        row.Find(".menu-trigger").Click();
        row.FindAll(".menu-item-action").First(r => r.TextContent.Contains("Delete")).Click();

        var dialog = _ctx.Render(dialogs.Current!);
        await dialog.Find("[data-testid=delete-cancel]").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        Assert.NotNull(await LoadAsync(chat.Id));
        Assert.Null(dialogs.Current);
    }

    [Fact]
    public async Task Deleting_the_open_chat_clears_the_selection_and_leaves_its_route()
    {
        // Otherwise the sidebar keeps highlighting a row that no longer exists and the page keeps showing
        // a conversation deleted out from under it.
        var chat = await SeedAsync("open one");
        var state = _ctx.Services.GetRequiredService<AppState>();
        state.SetActiveChat(chat.Id);
        var dialogs = _ctx.Services.GetRequiredService<DialogService>();
        var row = _ctx.RenderComponent<ChatRow>(p => p.Add(r => r.Chat, chat).Add(r => r.Active, true));
        row.Find(".menu-trigger").Click();
        row.FindAll(".menu-item-action").First(r => r.TextContent.Contains("Delete")).Click();

        var dialog = _ctx.Render(dialogs.Current!);
        await dialog.Find("[data-testid=delete-confirm]").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        Assert.Null(state.ActiveChatId);
        Assert.EndsWith("/", _ctx.Services.GetRequiredService<FakeNavigationManager>().Uri);
    }

    [Fact]
    public async Task A_write_tells_the_sidebar_the_list_changed()
    {
        // The row does not know which section is rendering it. Announcing through AppState is what makes
        // both the history list and a project reload themselves after a rename.
        var chat = await SeedAsync("first");
        var notified = 0;
        _ctx.Services.GetRequiredService<AppState>().ChatsChanged += () => notified++;
        var dialogs = _ctx.Services.GetRequiredService<DialogService>();
        var row = _ctx.RenderComponent<ChatRow>(p => p.Add(r => r.Chat, chat));
        row.Find(".menu-trigger").Click();
        row.FindAll(".menu-item-action").First(r => r.TextContent.Contains("Rename")).Click();

        var dialog = _ctx.Render(dialogs.Current!);
        dialog.Find("input").Change("second");
        await dialog.Find("[data-testid=name-save]").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        Assert.True(notified > 0);
    }

    [Fact]
    public async Task The_menu_trigger_names_the_chat_for_a_screen_reader()
    {
        // A sidebar of twenty identical "more actions" buttons is unusable without it.
        var chat = await SeedAsync("quarterly revenue");

        var row = _ctx.RenderComponent<ChatRow>(p => p.Add(r => r.Chat, chat));

        Assert.Contains("quarterly revenue", row.Find(".menu-trigger .sr-only").TextContent);
    }

    private async Task<ChatSummary> SeedAsync(string title)
    {
        using var scope = _ctx.Services.CreateScope();
        var chats = scope.ServiceProvider.GetRequiredService<ChatService>();
        var id = await chats.CreateChatAsync(title);
        return new ChatSummary(id, title, DateTime.UtcNow);
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

Run: `dotnet test tests/SqlAgent.Tests/SqlAgent.Tests.csproj --filter ChatRowTests`
Expected: FAIL — compile error, `ChatRow` does not exist.

- [ ] **Step 3: Write `ChatRow`**

Create `src/SqlAgent.Host/Components/Layout/ChatRow.razor`:

```razor
@inject ScopedRunner Runner
@inject AppState State
@inject DialogService Dialogs
@inject NavigationManager Nav

<div class="chat-row @(Active ? "active" : "")">
    @* Open() rather than an inline lambda: the URL is a string literal inside a Razor attribute, which
       needs escaping the parser handles badly. *@
    <button type="button" class="ghost chat-row-open truncate" @onclick="Open">@Chat.Title</button>
    <Menu Placement="MenuPlacement.Bottom">
        <Trigger>
            <Icon Name="more-vertical" Size="16" />
            <span class="sr-only">@($"Actions for {Chat.Title}")</span>
        </Trigger>
        <ChildContent>
            <MenuItem Icon="pencil" OnClick="ShowRename">Rename</MenuItem>
            <MenuItem Icon="trash" Danger="true" OnClick="ShowDelete">Delete</MenuItem>
        </ChildContent>
    </Menu>
</div>

@code {
    [Parameter, EditorRequired] public ChatSummary Chat { get; set; } = default!;

    /// <summary>Whether this is the conversation on screen. A parameter rather than a read of AppState
    /// here: a child that reads circuit state directly can be skipped by the renderer's diff when its own
    /// parameters have not changed, which strands the highlight on the chat the user just left.</summary>
    [Parameter] public bool Active { get; set; }

    private void Open() => Nav.NavigateTo($"/chat/{Chat.Id}");

    // The dialogs are handed to DialogService, which renders them from MainLayout. Rendering one here
    // would put a position:fixed element inside the sidebar, and below 1024px the sidebar's transform
    // makes it the containing block — the dialog would centre on the drawer and ride off-screen with it.
    private void ShowRename() => Dialogs.Show(
        @<NameDialog Title="Rename chat" Label="Title" InitialValue="@Chat.Title"
                     OnSave="RenameAsync" OnCancel="Dialogs.Close" />);

    private void ShowDelete() => Dialogs.Show(
        @<ChatDeleteDialog Chat="Chat" OnConfirm="DeleteAsync" OnCancel="Dialogs.Close" />);

    private async Task RenameAsync(string title)
    {
        await Runner.RunAsync<ChatService, bool>(s => s.RenameChatAsync(Chat.Id, title));
        Dialogs.Close();
        // This row does not know which section is rendering it. Announcing through AppState is what makes
        // the history list and every project reload themselves without the row referencing either.
        State.NotifyChatsChanged();
    }

    private async Task DeleteAsync()
    {
        await Runner.RunAsync<ChatService, bool>(s => s.DeleteChatAsync(Chat.Id));
        Dialogs.Close();
        // Clearing the selection first: leaving it set would keep highlighting a row that no longer
        // exists, and the page would keep showing a conversation deleted out from under it.
        if (State.ActiveChatId == Chat.Id)
        {
            State.SetActiveChat(null);
            Nav.NavigateTo("/");
        }
        State.NotifyChatsChanged();
    }
}
```

Create `src/SqlAgent.Host/Components/Layout/ChatRow.razor.css` by moving the row rules out of
`HistorySection.razor.css` and renaming `.history-row` to `.chat-row`:

```css
.chat-row { display: flex; align-items: center; gap: var(--space-1); border-radius: var(--radius-control); }
.chat-row:hover { background: var(--background-soft-100); }
.chat-row.active { background: var(--primary-50); }

.chat-row-open {
  flex: 1;
  min-width: 0;
  text-align: left;
  padding: var(--space-2);
  color: var(--text-50);
}
.chat-row.active .chat-row-open { color: var(--primary-500); font-weight: 500; }

/* The row menu appears on hover, and unconditionally while it is open or focused — a control that
   exists only under a mouse pointer is unreachable from the keyboard. */
.chat-row ::deep .menu-root { opacity: 0; }
.chat-row:hover ::deep .menu-root,
.chat-row:focus-within ::deep .menu-root { opacity: 1; }
```

`HistorySection.razor.css` keeps only its heading and empty-state rules; delete the `.history-row*` rules
it no longer renders.

- [ ] **Step 4: Reduce `HistorySection` to a list**

`src/SqlAgent.Host/Components/Layout/HistorySection.razor` becomes:

```razor
@implements IDisposable
@inject ScopedRunner Runner
@inject AppState State

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
                <ChatRow @key="chat.Id" Chat="chat" Active="@(chat.Id == State.ActiveChatId)" />
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
        // the same reason, and ChatRow's writes are what raise the event now.
        State.ChatsChanged += OnChatsChanged;
        await ReloadAsync();
    }

    private async Task ReloadAsync() =>
        _chats = await Runner.RunAsync<ChatService, IReadOnlyList<ChatSummary>>(s => s.ListHistoryAsync());

    private void OnChatsChanged() => InvokeAsync(async () =>
    {
        await ReloadAsync();
        StateHasChanged();
    });

    public void Dispose() => State.ChatsChanged -= OnChatsChanged;
}
```

`DialogService`, `NavigationManager`, `Open`, `ShowRename`, `ShowDelete`, `RenameAsync` and `DeleteAsync`
all move to `ChatRow`; delete them here.

Four tests in `tests/SqlAgent.Tests/HistorySectionTests.cs` are about behaviour that now lives in the row,
and `ChatRowTests` covers each of them against the same store: delete
`Renaming_from_the_row_menu_goes_through_a_dialog_and_updates_the_store`,
`Deleting_from_the_row_menu_asks_first_and_then_removes_the_chat`,
`Cancelling_the_delete_dialog_keeps_the_chat` and
`Deleting_the_chat_that_is_open_clears_the_selection`. Keep the rest, and in
`The_open_chat_is_marked_active` change the selector `.history-row` to `.chat-row`. Nothing else in that
file changes — the bucketing, empty-state and reload-on-notification tests are exactly what must keep
passing to show the extraction changed nothing.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/SqlAgent.Tests/SqlAgent.Tests.csproj --filter "ChatRowTests|HistorySectionTests"`
Expected: PASS.

- [ ] **Step 6: Run the whole suite**

Run: `dotnet test SqlAgent.slnx --configuration Release`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add src/SqlAgent.Host/Components/Layout tests/SqlAgent.Tests
git commit -m "$(cat <<'COMMIT'
Give a chat's row its own actions, so two sections can render it

The project list shows chats exactly as the history list does. Leaving the row
presentational would have meant copying its rename and delete handlers into a
second section — thirty lines of dialog-and-write logic in two files, which is
the duplication a reviewer is required to raise and the shape this project has
already paid for twice.

So the row owns what its menu does: it resolves its own services, opens its own
dialogs, writes, and announces through AppState. Neither section knows the other
exists, and both reload themselves off the same event.

Active stays a parameter rather than a read of AppState inside the row: a child
that reads circuit state directly can be skipped by the renderer's diff when its
own parameters have not changed, stranding the highlight on the chat you left.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
COMMIT
)"
```

---

### Task 5: The project section

**Files:**
- Create: `src/SqlAgent.Host/Components/Layout/ProjectSection.razor` + `.razor.css`
- Create: `src/SqlAgent.Host/Components/Shared/Chat/MoveToProjectDialog.razor`
- Create: `src/SqlAgent.Host/Components/Shared/Chat/ProjectDeleteDialog.razor`
- Modify: `src/SqlAgent.Host/Components/Shared/Chat/NameDialog.razor` (an `Error` parameter)
- Modify: `src/SqlAgent.Host/Components/Layout/ChatRow.razor` (the Move item)
- Modify: `src/SqlAgent.Host/Components/Layout/Sidebar.razor` (render the section)
- Modify: `src/SqlAgent.Host/Components/Shared/Ui/Icon.razor` (`folder`, `chevron-right`)
- Modify: `tests/SqlAgent.Tests/UiPrimitiveTests.cs` (the icon inventory), `tests/SqlAgent.Tests/ShellTests.cs` (one registration)
- Test: `tests/SqlAgent.Tests/ProjectSectionTests.cs`

**Interfaces:**
- Consumes: `ProjectService` and its records (Task 1), `ChatRow` (Task 4), `NameDialog` (Task 3), `DialogService`, `AppState`.
- Produces:
  - `<ProjectSection />`, rendered by `Sidebar` above `HistorySection`.
  - `<MoveToProjectDialog Chat="ChatSummary" Projects="IReadOnlyList<ProjectSummary>" CurrentProjectId="Guid?" OnPick="EventCallback<Guid?>" OnCancel="EventCallback" />`
  - `<ProjectDeleteDialog Project="ProjectSummary" OnConfirm="EventCallback<ProjectDeleteMode>" OnCancel="EventCallback" />`
  - `NameDialog` gains `Error="string?"`, rendered beside the field when set.
  - Icon names `folder` and `chevron-right`.

`search` is **not** added here — Task 7 is what renders it, and
`UiPrimitiveTests.No_icon_ships_that_nothing_renders` fails on a glyph with no caller.

- [ ] **Step 1: Write the failing test**

Create `tests/SqlAgent.Tests/ProjectSectionTests.cs`:

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

public class ProjectSectionTests : IDisposable
{
    private readonly SqliteConnection _conn = new("DataSource=:memory:");
    private readonly Bunit.TestContext _ctx = new();

    public ProjectSectionTests()
    {
        _conn.Open();
        _ctx.Services.AddDbContext<SqlAgentDbContext>(o => o.UseSqlite(_conn));
        _ctx.Services.AddScoped<ChatService>();
        _ctx.Services.AddScoped<ProjectService>();
        _ctx.Services.AddScoped<ScopedRunner>();
        _ctx.Services.AddScoped<AppState>();
        _ctx.Services.AddScoped<DialogService>();
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        using var scope = _ctx.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<SqlAgentDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public void With_no_projects_only_the_heading_and_its_add_button_render()
    {
        // "No projects yet" tells the user nothing they cannot already see, and the history section
        // below already covers the genuinely empty case.
        var section = _ctx.RenderComponent<ProjectSection>();

        Assert.Empty(section.FindAll(".project-row"));
        Assert.Single(section.FindAll("[data-testid=project-add]"));
        Assert.DoesNotContain("No projects", section.Markup);
    }

    [Fact]
    public async Task A_project_shows_its_name_and_how_many_chats_are_in_it()
    {
        await SeedProjectAsync("quarterly", "first", "second");

        var section = _ctx.RenderComponent<ProjectSection>();

        Assert.Contains("quarterly", section.Markup);
        Assert.Contains("2", section.Find(".project-count").TextContent);
    }

    [Fact]
    public async Task A_project_is_collapsed_until_it_is_opened()
    {
        // Expanding every project on load would bury the history section under everything the user has
        // ever filed.
        var id = await SeedProjectAsync("quarterly", "first");
        var section = _ctx.RenderComponent<ProjectSection>();

        Assert.Empty(section.FindAll(".chat-row"));

        await section.Find(".project-open").ClickAsync(new MouseEventArgs());
        Assert.Single(section.FindAll(".chat-row"));

        await section.Find(".project-open").ClickAsync(new MouseEventArgs());
        Assert.Empty(section.FindAll(".chat-row"));
    }

    [Fact]
    public async Task Creating_a_project_goes_through_a_dialog_and_appears_in_the_list()
    {
        var dialogs = _ctx.Services.GetRequiredService<DialogService>();
        var section = _ctx.RenderComponent<ProjectSection>();

        section.Find("[data-testid=project-add]").Click();
        var dialog = _ctx.Render(dialogs.Current!);
        dialog.Find("input").Change("quarterly");
        await dialog.Find("[data-testid=name-save]").ClickAsync(new MouseEventArgs());

        Assert.Contains("quarterly", section.Markup);
        Assert.Null(dialogs.Current);
    }

    [Fact]
    public async Task A_name_already_taken_is_reported_in_the_dialog_which_stays_open()
    {
        // The alternative — closing the dialog and quietly doing nothing — is how a user concludes the
        // button is broken.
        await SeedProjectAsync("quarterly");
        var dialogs = _ctx.Services.GetRequiredService<DialogService>();
        var section = _ctx.RenderComponent<ProjectSection>();

        section.Find("[data-testid=project-add]").Click();
        var dialog = _ctx.Render(dialogs.Current!);
        dialog.Find("input").Change("quarterly");
        await dialog.Find("[data-testid=name-save]").ClickAsync(new MouseEventArgs());

        Assert.NotNull(dialogs.Current);
        Assert.Contains("already", _ctx.Render(dialogs.Current!).Markup, StringComparison.OrdinalIgnoreCase);
        Assert.Single(await ListProjectsAsync());
    }

    [Fact]
    public async Task Deleting_a_project_offers_both_outcomes_and_keeping_the_chats_returns_them()
    {
        var id = await SeedProjectAsync("quarterly", "kept");
        var dialogs = _ctx.Services.GetRequiredService<DialogService>();
        var section = _ctx.RenderComponent<ProjectSection>();

        section.Find(".project-row .menu-trigger").Click();
        section.FindAll(".menu-item-action").First(r => r.TextContent.Contains("Delete")).Click();

        var dialog = _ctx.Render(dialogs.Current!);
        Assert.Contains("quarterly", dialog.Markup);
        await dialog.Find("[data-testid=project-delete-keep]").ClickAsync(new MouseEventArgs());

        Assert.Empty(await ListProjectsAsync());
        using var scope = _ctx.Services.CreateScope();
        var history = await scope.ServiceProvider.GetRequiredService<ChatService>().ListHistoryAsync();
        Assert.Contains(history, c => c.Title == "kept");
    }

    [Fact]
    public async Task Deleting_a_project_with_its_chats_takes_them_too()
    {
        await SeedProjectAsync("quarterly", "doomed");
        var dialogs = _ctx.Services.GetRequiredService<DialogService>();
        var section = _ctx.RenderComponent<ProjectSection>();
        section.Find(".project-row .menu-trigger").Click();
        section.FindAll(".menu-item-action").First(r => r.TextContent.Contains("Delete")).Click();

        var dialog = _ctx.Render(dialogs.Current!);
        await dialog.Find("[data-testid=project-delete-with-chats]").ClickAsync(new MouseEventArgs());

        Assert.Empty(await ListProjectsAsync());
        using var scope = _ctx.Services.CreateScope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<ChatService>().ListHistoryAsync());
    }

    [Fact]
    public async Task The_list_re_reads_itself_when_something_says_the_chats_changed()
    {
        // A chat moved into a project from the history section's own row has to change this section's
        // counts, and the two are siblings that only meet through AppState.
        var section = _ctx.RenderComponent<ProjectSection>();
        await SeedProjectAsync("brand new");

        await section.InvokeAsync(_ctx.Services.GetRequiredService<AppState>().NotifyChatsChanged);

        Assert.Contains("brand new", section.Markup);
    }

    private async Task<Guid> SeedProjectAsync(string name, params string[] chatTitles)
    {
        using var scope = _ctx.Services.CreateScope();
        var projects = scope.ServiceProvider.GetRequiredService<ProjectService>();
        var chats = scope.ServiceProvider.GetRequiredService<ChatService>();
        var created = await projects.CreateProjectAsync(name);
        foreach (var title in chatTitles)
            await projects.MoveChatAsync(await chats.CreateChatAsync(title), created.Id!.Value);
        return created.Id!.Value;
    }

    private async Task<IReadOnlyList<ProjectSummary>> ListProjectsAsync()
    {
        using var scope = _ctx.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ProjectService>().ListProjectsAsync();
    }

    public void Dispose()
    {
        _ctx.Dispose();
        _conn.Dispose();
    }
}
```

Add one fact to `tests/SqlAgent.Tests/ChatRowTests.cs`:

```csharp
    [Fact]
    public async Task Moving_a_chat_into_a_project_takes_it_out_of_the_history_list()
    {
        var chat = await SeedAsync("wandering");
        Guid projectId;
        using (var scope = _ctx.Services.CreateScope())
            projectId = (await scope.ServiceProvider.GetRequiredService<ProjectService>()
                .CreateProjectAsync("quarterly")).Id!.Value;
        var dialogs = _ctx.Services.GetRequiredService<DialogService>();
        var row = _ctx.RenderComponent<ChatRow>(p => p.Add(r => r.Chat, chat));

        row.Find(".menu-trigger").Click();
        row.FindAll(".menu-item-action").First(r => r.TextContent.Contains("Move")).Click();

        var dialog = _ctx.Render(dialogs.Current!);
        await dialog.FindAll("[data-testid=move-target]")
            .First(b => b.TextContent.Contains("quarterly"))
            .ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        using var check = _ctx.Services.CreateScope();
        var chats = check.ServiceProvider.GetRequiredService<ChatService>();
        Assert.DoesNotContain(await chats.ListHistoryAsync(), c => c.Id == chat.Id);
    }
```

`ChatRowTests`'s fixture needs `ProjectService` registered for it; add
`_ctx.Services.AddScoped<ProjectService>();` beside the others in its constructor.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/SqlAgent.Tests/SqlAgent.Tests.csproj --filter "ProjectSectionTests|ChatRowTests"`
Expected: FAIL — compile error, `ProjectSection` and the two dialogs do not exist.

- [ ] **Step 3: Add the two glyphs**

In `src/SqlAgent.Host/Components/Shared/Ui/Icon.razor`, add to `Paths`:

```csharp
        // Phase B2: the project section's folder and its collapsed chevron.
        ["folder"] = ["M3 7 A1 1 0 0 1 4 6 H9.5 L11.5 8.5 H20 A1 1 0 0 1 21 9.5 V18 A1 1 0 0 1 20 19 H4 A1 1 0 0 1 3 18 Z"],
        ["chevron-right"] = ["M9.5 6 L15.5 12 L9.5 18"],
```

Add `"folder"` and `"chevron-right"` to the `rendered` array in
`UiPrimitiveTests.No_icon_ships_that_nothing_renders` and to the `[InlineData]` list on
`The_icons_the_shell_needs_all_exist`.

- [ ] **Step 4: Give `NameDialog` an error line**

In `src/SqlAgent.Host/Components/Shared/Chat/NameDialog.razor`, add the parameter and render it under the
input:

```razor
        @if (!string.IsNullOrWhiteSpace(Error))
        {
            <p class="name-error" role="alert">@Error</p>
        }
```

```csharp
    /// <summary>Shown under the field when the caller refuses the name — a project name already taken,
    /// today. The dialog stays open when this is set: closing it and quietly doing nothing is how a user
    /// concludes the button is broken.</summary>
    [Parameter] public string? Error { get; set; }
```

Create `src/SqlAgent.Host/Components/Shared/Chat/NameDialog.razor.css`:

```css
.name-error { margin-top: var(--space-2); color: var(--danger-500); font-size: var(--text-xs); }
```

- [ ] **Step 5: Write the two dialogs**

Create `src/SqlAgent.Host/Components/Shared/Chat/MoveToProjectDialog.razor`:

```razor
@* Presentational: it reports which project was picked. The caller owns the write, as every dialog in
   this codebase does. The project list is passed in rather than fetched here so the dialog needs no
   service scope of its own. *@
<Modal Title="Move to project" OnClose="OnCancel">
    <ChildContent>
        <p class="muted">Move <strong>@Chat.Title</strong> to:</p>
        <div class="move-targets">
            @* "No project" first and always present: taking a chat back out is the same operation as
               moving it, and burying it under the project list would hide the only way back. *@
            <button type="button" class="ghost move-target" data-testid="move-target"
                    disabled="@(CurrentProjectId is null)"
                    @onclick="() => OnPick.InvokeAsync(null)">No project</button>
            @foreach (var project in Projects)
            {
                <button type="button" class="ghost move-target" data-testid="move-target"
                        disabled="@(CurrentProjectId == project.Id)"
                        @onclick="() => OnPick.InvokeAsync(project.Id)">@project.Name</button>
            }
        </div>
    </ChildContent>
    <Footer>
        <button type="button" data-testid="move-cancel" @onclick="OnCancel">Cancel</button>
    </Footer>
</Modal>

@code {
    [Parameter, EditorRequired] public ChatSummary Chat { get; set; } = default!;
    [Parameter] public IReadOnlyList<ProjectSummary> Projects { get; set; } = [];

    /// <summary>Where the chat is now, so that row is shown as the current one rather than offered as a
    /// move that would do nothing.</summary>
    [Parameter] public Guid? CurrentProjectId { get; set; }

    [Parameter] public EventCallback<Guid?> OnPick { get; set; }
    [Parameter] public EventCallback OnCancel { get; set; }
}
```

Create `src/SqlAgent.Host/Components/Shared/Chat/ProjectDeleteDialog.razor`:

```razor
@* Two outcomes, both spelled out. This is the only operation in the phase that can destroy a
   conversation, so neither answer is a default and neither is hidden behind the other. *@
<Modal Title="Delete project" OnClose="OnCancel">
    <ChildContent>
        <p>Delete <strong>@Project.Name</strong>?</p>
        <p class="muted">
            It holds @Project.ChatCount @(Project.ChatCount == 1 ? "chat" : "chats").
            Keeping them returns them to your history.
        </p>
    </ChildContent>
    <Footer>
        <button type="button" data-testid="project-delete-cancel" @onclick="OnCancel">Cancel</button>
        <button type="button" data-testid="project-delete-keep"
                @onclick="() => OnConfirm.InvokeAsync(ProjectDeleteMode.KeepChats)">Delete, keep the chats</button>
        <button type="button" class="danger" data-testid="project-delete-with-chats"
                @onclick="() => OnConfirm.InvokeAsync(ProjectDeleteMode.DeleteChats)">Delete the chats too</button>
    </Footer>
</Modal>

@code {
    [Parameter, EditorRequired] public ProjectSummary Project { get; set; } = default!;
    [Parameter] public EventCallback<ProjectDeleteMode> OnConfirm { get; set; }
    [Parameter] public EventCallback OnCancel { get; set; }
}
```

- [ ] **Step 6: Add the Move item to `ChatRow`**

In `src/SqlAgent.Host/Components/Layout/ChatRow.razor`, add the menu item between Rename and Delete:

```razor
            <MenuItem Icon="folder" OnClick="ShowMoveAsync">Move to project</MenuItem>
```

a parameter, and the handlers:

```csharp
    /// <summary>The project this row is being rendered under, or null in the history list. Used only to
    /// grey out the move that would do nothing. It is a parameter rather than something the row looks up
    /// because `ChatSummary` does not carry it, and adding it to that record would change a shape four
    /// other call sites already use.</summary>
    [Parameter] public Guid? CurrentProjectId { get; set; }

    /// <summary>The project list is read before the dialog opens rather than inside it, so the dialog
    /// stays presentational with no service scope of its own — the same shape every other dialog in this
    /// codebase has.</summary>
    private async Task ShowMoveAsync()
    {
        var projects = await Runner.RunAsync<ProjectService, IReadOnlyList<ProjectSummary>>(
            s => s.ListProjectsAsync());
        Dialogs.Show(
            @<MoveToProjectDialog Chat="Chat" Projects="projects" CurrentProjectId="CurrentProjectId"
                                  OnPick="MoveAsync" OnCancel="Dialogs.Close" />);
    }

    private async Task MoveAsync(Guid? projectId)
    {
        await Runner.RunAsync<ProjectService, bool>(s => s.MoveChatAsync(Chat.Id, projectId));
        Dialogs.Close();
        State.NotifyChatsChanged();
    }
```

- [ ] **Step 7: Write `ProjectSection`**

Create `src/SqlAgent.Host/Components/Layout/ProjectSection.razor`:

```razor
@implements IDisposable
@inject ScopedRunner Runner
@inject AppState State
@inject DialogService Dialogs

<div class="projects">
    <div class="projects-head">
        <p class="projects-label">Projects</p>
        <button type="button" class="ghost projects-add" data-testid="project-add"
                @onclick="ShowCreate" aria-label="New project">
            <Icon Name="plus" Size="16" />
        </button>
    </div>

    @foreach (var project in _projects)
    {
        <div class="project-row" @key="project.Id">
            <button type="button" class="ghost project-open truncate" @onclick="() => ToggleAsync(project)">
                @* Two elements under an @if rather than one with a ternary Name. The inventory test in
                   Task 8 scans this markup for the glyph names actually rendered, and a name computed
                   inside an attribute expression is not something a regex can read honestly — the
                   alternative was a scanner complicated enough to have its own bugs. *@
                @if (_expanded.Contains(project.Id))
                {
                    <Icon Name="chevron-down" Size="14" />
                }
                else
                {
                    <Icon Name="chevron-right" Size="14" />
                }
                <Icon Name="folder" Size="16" />
                <span class="truncate">@project.Name</span>
                <span class="project-count">@project.ChatCount</span>
            </button>
            <Menu Placement="MenuPlacement.Bottom">
                <Trigger>
                    <Icon Name="more-vertical" Size="16" />
                    <span class="sr-only">@($"Actions for {project.Name}")</span>
                </Trigger>
                <ChildContent>
                    <MenuItem Icon="pencil" OnClick="() => ShowRename(project)">Rename</MenuItem>
                    <MenuItem Icon="trash" Danger="true" OnClick="() => ShowDelete(project)">Delete</MenuItem>
                </ChildContent>
            </Menu>
        </div>

        @if (_expanded.Contains(project.Id))
        {
            <div class="project-chats">
                @foreach (var chat in _chats[project.Id])
                {
                    <ChatRow @key="chat.Id" Chat="chat" CurrentProjectId="project.Id"
                             Active="@(chat.Id == State.ActiveChatId)" />
                }
            </div>
        }
    }
</div>

@code {
    private IReadOnlyList<ProjectSummary> _projects = [];

    // Which projects are open, and the chats loaded for them. Circuit state only: persisting it would
    // mean a third localStorage key beside the theme and the sidebar, for something one click restores.
    private readonly HashSet<Guid> _expanded = [];
    private readonly Dictionary<Guid, IReadOnlyList<ChatSummary>> _chats = [];

    protected override async Task OnInitializedAsync()
    {
        // A chat moved into a project from the history list changes this section's counts, and the two
        // are siblings that meet only through AppState — the same reason HistorySection subscribes.
        State.ChatsChanged += OnChatsChanged;
        await ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        _projects = await Runner.RunAsync<ProjectService, IReadOnlyList<ProjectSummary>>(
            s => s.ListProjectsAsync());
        // Only the open ones, and only the ones that still exist: a project deleted elsewhere must not
        // leave its chats cached under an id nothing renders.
        foreach (var id in _expanded.ToList())
        {
            if (!_projects.Any(p => p.Id == id)) { _expanded.Remove(id); _chats.Remove(id); continue; }
            _chats[id] = await Runner.RunAsync<ProjectService, IReadOnlyList<ChatSummary>>(
                s => s.ListChatsInProjectAsync(id));
        }
    }

    private void OnChatsChanged() => InvokeAsync(async () =>
    {
        await ReloadAsync();
        StateHasChanged();
    });

    private async Task ToggleAsync(ProjectSummary project)
    {
        if (!_expanded.Add(project.Id))
        {
            _expanded.Remove(project.Id);
            _chats.Remove(project.Id);
            return;
        }
        _chats[project.Id] = await Runner.RunAsync<ProjectService, IReadOnlyList<ChatSummary>>(
            s => s.ListChatsInProjectAsync(project.Id));
    }

    private void ShowCreate() => ShowNameDialog("New project", "Create", "", CreateAsync);

    private void ShowRename(ProjectSummary project) =>
        ShowNameDialog("Rename project", "Save", project.Name,
            name => RenameAsync(project, name));

    /// <summary>One dialog for both writes, re-shown with an error when the name is taken. Re-showing
    /// replaces the open dialog rather than stacking a second one — DialogService holds exactly one.</summary>
    private void ShowNameDialog(string title, string confirm, string initial,
        Func<string, Task<ProjectWriteOutcome>> write, string? error = null)
    {
        Dialogs.Show(
            @<NameDialog Title="@title" Label="Name" InitialValue="@initial" ConfirmLabel="@confirm"
                         Error="@error" OnCancel="Dialogs.Close"
                         OnSave="@(async name =>
                         {
                             var outcome = await write(name);
                             if (outcome == ProjectWriteOutcome.NameTaken)
                             {
                                 ShowNameDialog(title, confirm, name, write,
                                     $"A project called \"{name}\" already exists.");
                                 return;
                             }
                             Dialogs.Close();
                             await ReloadAsync();
                             StateHasChanged();
                         })" />);
    }

    private async Task<ProjectWriteOutcome> CreateAsync(string name) =>
        (await Runner.RunAsync<ProjectService, ProjectWriteResult>(s => s.CreateProjectAsync(name))).Outcome;

    private async Task<ProjectWriteOutcome> RenameAsync(ProjectSummary project, string name) =>
        (await Runner.RunAsync<ProjectService, ProjectWriteResult>(
            s => s.RenameProjectAsync(project.Id, name))).Outcome;

    private void ShowDelete(ProjectSummary project) => Dialogs.Show(
        @<ProjectDeleteDialog Project="project" OnCancel="Dialogs.Close"
                              OnConfirm="mode => DeleteAsync(project, mode)" />);

    private async Task DeleteAsync(ProjectSummary project, ProjectDeleteMode mode)
    {
        await Runner.RunAsync<ProjectService, bool>(s => s.DeleteProjectAsync(project.Id, mode));
        Dialogs.Close();
        _expanded.Remove(project.Id);
        _chats.Remove(project.Id);
        await ReloadAsync();
        // Deleting with the chats removes rows the history section is not showing, but keeping them adds
        // rows it is — either way it has to re-read.
        State.NotifyChatsChanged();
        StateHasChanged();
    }

    public void Dispose() => State.ChatsChanged -= OnChatsChanged;
}
```

Create `src/SqlAgent.Host/Components/Layout/ProjectSection.razor.css`:

```css
.projects { display: flex; flex-direction: column; gap: 2px; margin-bottom: var(--space-4); }

.projects-head { display: flex; align-items: center; justify-content: space-between; }
.projects-label {
  padding: 0 var(--space-2);
  color: var(--text-100);
  font-size: var(--text-xs);
  font-weight: 500;
}
.projects-add { padding: var(--space-1); line-height: 0; }

.project-row { display: flex; align-items: center; gap: var(--space-1); border-radius: var(--radius-control); }
.project-row:hover { background: var(--background-soft-100); }

.project-open {
  flex: 1;
  min-width: 0;
  display: flex;
  align-items: center;
  gap: var(--space-2);
  text-align: left;
  padding: var(--space-2);
  color: var(--text-50);
}
.project-count { margin-left: auto; color: var(--text-100); font-size: var(--text-xs); }

/* Indented so a chat reads as belonging to the project above it rather than as a sibling of it. */
.project-chats { padding-left: var(--space-4); }

/* Same reasoning as the chat row's menu: visible on hover, and unconditionally once open or focused,
   because a control that exists only under a pointer is unreachable from the keyboard. */
.project-row ::deep .menu-root { opacity: 0; }
.project-row:hover ::deep .menu-root,
.project-row:focus-within ::deep .menu-root { opacity: 1; }
```

- [ ] **Step 8: Put it in the sidebar**

In `src/SqlAgent.Host/Components/Layout/Sidebar.razor`, inside `.sidebar-body`, above `HistorySection`:

```razor
        <ProjectSection />
        <HistorySection />
```

`ShellTests` renders `Sidebar`, so add `ctx.Services.AddScoped<ProjectService>();` to its
`RegisterSidebarServices` beside `ChatService`.

- [ ] **Step 9: Run the tests to verify they pass**

Run: `dotnet test tests/SqlAgent.Tests/SqlAgent.Tests.csproj --filter "ProjectSectionTests|ChatRowTests|ShellTests|UiPrimitiveTests"`
Expected: PASS.

- [ ] **Step 10: Run the whole suite**

Run: `dotnet test SqlAgent.slnx --configuration Release`
Expected: PASS.

- [ ] **Step 11: Commit**

```bash
git add src/SqlAgent.Host tests/SqlAgent.Tests
git commit -m "$(cat <<'COMMIT'
List projects in the sidebar, with move, rename and a two-outcome delete

A project row carries its name, its chat count and a chevron; expanding it lists
its chats through the same ChatRow the history section uses. Expansion is circuit
state — persisting it would mean a third localStorage key beside the theme and
the sidebar, for something one click restores.

Deleting a project is the only operation in this phase that can destroy a
conversation, so the dialog spells out both outcomes and defaults to neither.
A name already taken re-opens the dialog with the message beside the field
instead of closing it and quietly doing nothing, which is how a user concludes
the button is broken.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
COMMIT
)"
```

---

### Task 6: One document listener, and an Escape that works in Safari

**Files:**
- Create: `src/SqlAgent.Host/Web/ShortcutService.cs`
- Create: `src/SqlAgent.Host/wwwroot/js/shortcuts.js`
- Create: `src/SqlAgent.Host/Components/Layout/KeyboardShortcuts.razor`
- Modify: `src/SqlAgent.Host/Components/App.razor` (load the script), `Components/Layout/MainLayout.razor` (render the component), `Program.cs` (register the service)
- Modify: `src/SqlAgent.Host/Components/Shared/Ui/Menu.razor`, `Modal.razor` (subscribe while open)
- Modify: seven test fixtures that render a `Menu` or a `Modal`; see Step 6
- Test: `tests/SqlAgent.Tests/ShortcutServiceTests.cs`

**Interfaces:**
- Consumes: nothing new.
- Produces:
  - `ShortcutService.EscapePressed: event Action`, `ShortcutService.SearchRequested: event Action`, `RaiseEscape()`, `RaiseSearch()` — circuit-scoped.
  - `KeyboardShortcuts.OnEscape()` and `OnSearch()` — `[JSInvokable]`, called by `shortcuts.js`.
  - `window.sqlAgentShortcuts.bind(dotNetRef)` / `unbind()`.

**Why this exists.** `Ctrl`/`Cmd`+`K` must work wherever focus is, and Blazor only hears elements it
rendered — so a document-level listener is unavoidable. Once it exists it also closes Phase A's carried
Safari defect: Safari does not focus a `<button>` on a plain mouse click (a macOS convention), so a menu
opened by mouse has nothing for Escape to bubble from and cannot be dismissed from the keyboard there.
`Menu` and `Modal` keep their own local handlers — those work everywhere else and cost nothing — and add
a subscription **while open**, which is the same conditional-attachment discipline Phase A applied to the
drawer's key handler.

- [ ] **Step 1: Write the failing test**

Create `tests/SqlAgent.Tests/ShortcutServiceTests.cs`:

```csharp
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using SqlAgent.Host.Components.Shared.Ui;
using SqlAgent.Host.Web;

namespace SqlAgent.Tests;

/// <summary>
/// The service exists so one document-level listener can reach whatever is open. bUnit runs no browser,
/// so the listener itself is a manual check; what is testable here — and what actually broke in Safari —
/// is whether an open popover is listening and a closed one is not.
/// </summary>
public class ShortcutServiceTests
{
    [Fact]
    public void An_open_menu_closes_on_a_global_escape()
    {
        // Safari does not focus a button on a plain mouse click, so Menu's own keydown handler never
        // fires there: the keypress goes to the document and nothing in the menu hears it.
        using var ctx = NewContext(out var shortcuts);
        var menu = ctx.RenderComponent<Menu>(p => p
            .Add(m => m.Trigger, (RenderFragment)(b => b.AddMarkupContent(0, "<span>t</span>")))
            .AddChildContent("<div id=\"body\">contents</div>"));
        menu.Find(".menu-trigger").Click();
        Assert.Single(menu.FindAll("#body"));

        menu.InvokeAsync(shortcuts.RaiseEscape);

        Assert.Empty(menu.FindAll("#body"));
    }

    [Fact]
    public void A_closed_menu_is_not_listening()
    {
        // Every menu in the sidebar would otherwise hold a subscription for the life of the circuit, and
        // a global Escape would run one handler per row. The same conditional-attachment discipline
        // Phase A applied to the drawer's keydown handler.
        using var ctx = NewContext(out var shortcuts);
        var menu = ctx.RenderComponent<Menu>(p => p
            .Add(m => m.Trigger, (RenderFragment)(b => b.AddMarkupContent(0, "<span>t</span>")))
            .AddChildContent("<div id=\"body\">contents</div>"));

        menu.Find(".menu-trigger").Click();
        menu.Find(".menu-backdrop").Click();
        Assert.Empty(menu.FindAll("#body"));

        // Raising it again must not reopen anything or throw into a component that stopped listening.
        menu.InvokeAsync(shortcuts.RaiseEscape);

        Assert.Empty(menu.FindAll("#body"));
    }

    [Fact]
    public void An_open_modal_closes_on_a_global_escape()
    {
        using var ctx = NewContext(out var shortcuts);
        var closes = 0;
        var modal = ctx.RenderComponent<Modal>(p => p
            .Add(m => m.Title, "About")
            .Add(m => m.OnClose, EventCallback.Factory.Create(new object(), () => closes++))
            .AddChildContent("<p>body</p>"));

        modal.InvokeAsync(shortcuts.RaiseEscape);

        Assert.Equal(1, closes);
    }

    [Fact]
    public void A_disposed_component_stops_listening()
    {
        // A leaked subscription keeps a torn-down component alive for the rest of the circuit — the same
        // leak ShellTests pins for Sidebar's LocationChanged handler.
        using var ctx = NewContext(out var shortcuts);
        var closes = 0;
        ctx.RenderComponent<Modal>(p => p
            .Add(m => m.Title, "About")
            .Add(m => m.OnClose, EventCallback.Factory.Create(new object(), () => closes++))
            .AddChildContent("<p>body</p>"));

        ctx.DisposeComponents();
        shortcuts.RaiseEscape();

        Assert.Equal(0, closes);
    }

    [Fact]
    public void The_search_request_reaches_whoever_is_listening()
    {
        var shortcuts = new ShortcutService();
        var asked = 0;
        shortcuts.SearchRequested += () => asked++;

        shortcuts.RaiseSearch();

        Assert.Equal(1, asked);
    }

    private static Bunit.TestContext NewContext(out ShortcutService shortcuts)
    {
        var ctx = new Bunit.TestContext();
        shortcuts = new ShortcutService();
        ctx.Services.AddSingleton(shortcuts);
        return ctx;
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/SqlAgent.Tests/SqlAgent.Tests.csproj --filter ShortcutServiceTests`
Expected: FAIL — compile error, `ShortcutService` does not exist.

- [ ] **Step 3: Write the service**

Create `src/SqlAgent.Host/Web/ShortcutService.cs`:

```csharp
namespace SqlAgent.Host.Web;

/// <summary>
/// Keyboard events that have to reach components regardless of where focus is, fed by the single
/// document-level listener in <c>wwwroot/js/shortcuts.js</c>.
///
/// Escape is here because Safari does not focus a <c>&lt;button&gt;</c> on a plain mouse click — a macOS
/// convention — so a popover opened by mouse has nothing for a bubbling Escape to start from and cannot
/// be dismissed from the keyboard. Ctrl/Cmd+K is here because a shortcut that only works when focus
/// happens to be inside the app's own markup is not a shortcut.
///
/// Scoped to the circuit, like AppState and DialogService: one browser tab, one set of subscribers.
/// </summary>
public sealed class ShortcutService
{
    /// <summary>Raised on Escape anywhere in the document. Subscribers attach while they are open and
    /// detach when they close, so a sidebar of twenty menus is not twenty live handlers.</summary>
    public event Action? EscapePressed;

    /// <summary>Raised on Ctrl/Cmd+K anywhere in the document.</summary>
    public event Action? SearchRequested;

    public void RaiseEscape() => EscapePressed?.Invoke();

    public void RaiseSearch() => SearchRequested?.Invoke();
}
```

- [ ] **Step 4: Write the script and its component**

Create `src/SqlAgent.Host/wwwroot/js/shortcuts.js`:

```js
// One document-level keydown listener for the whole app. Blazor only hears events on elements it
// rendered, so a shortcut that must work wherever focus is cannot be expressed in Razor at all — the
// same reason sql-editor.js and composer.js exist.
window.sqlAgentShortcuts = {
  bind: function (dotNetRef) {
    const onKeyDown = function (e) {
      if (e.key === 'Escape') {
        // Not prevented: an Escape that also dismisses a native autocomplete or an IME candidate list is
        // the browser's business, and this handler only tells C# it happened.
        dotNetRef.invokeMethodAsync('OnEscape').catch(() => {});
        return;
      }
      if ((e.ctrlKey || e.metaKey) && (e.key === 'k' || e.key === 'K')) {
        // Prevented: Chrome puts Ctrl+K in the address bar and Firefox in its search field, so without
        // this the app's own shortcut loses to the browser's every time.
        e.preventDefault();
        dotNetRef.invokeMethodAsync('OnSearch').catch(() => {});
      }
    };

    document.addEventListener('keydown', onKeyDown);
    // Kept so unbind removes exactly this listener; a circuit that reconnects must not leave the old one
    // attached to a dead DotNetObjectReference. The catch above covers the window between a dropped
    // circuit and the unbind that follows it.
    window._sqlAgentShortcutHandler = onKeyDown;
  },

  unbind: function () {
    if (!window._sqlAgentShortcutHandler) return;
    document.removeEventListener('keydown', window._sqlAgentShortcutHandler);
    delete window._sqlAgentShortcutHandler;
  },
};
```

Create `src/SqlAgent.Host/Components/Layout/KeyboardShortcuts.razor` — it renders nothing and exists to
own the interop:

```razor
@implements IAsyncDisposable
@inject IJSRuntime JS
@inject ShortcutService Shortcuts
@inject ILogger<KeyboardShortcuts> Logger

@code {
    private DotNetObjectReference<KeyboardShortcuts>? _self;

    [JSInvokable]
    public void OnEscape() => Shortcuts.RaiseEscape();

    [JSInvokable]
    public void OnSearch() => Shortcuts.RaiseSearch();

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender) return;
        _self = DotNetObjectReference.Create(this);
        try
        {
            await JS.InvokeVoidAsync("sqlAgentShortcuts.bind", _self);
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException or OperationCanceledException)
        {
            // Losing the binding costs the global shortcut and Safari's Escape; every menu still closes
            // by click and by its own local handler, so there is nothing to recover and nothing worth
            // telling the user. Level split by meaning, as everywhere else here.
            Logger.Log(ex is JSException ? LogLevel.Warning : LogLevel.Debug, ex,
                "sqlAgentShortcuts.bind failed; Ctrl/Cmd+K and document-level Escape are unavailable.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_self is not null)
        {
            try
            {
                await JS.InvokeVoidAsync("sqlAgentShortcuts.unbind");
            }
            catch (Exception ex) when (ex is JSException or JSDisconnectedException or OperationCanceledException)
            {
                Logger.Log(ex is JSException ? LogLevel.Warning : LogLevel.Debug, ex,
                    "sqlAgentShortcuts.unbind failed.");
            }
            finally
            {
                _self.Dispose();
                _self = null;
            }
        }
    }
}
```

In `src/SqlAgent.Host/Components/App.razor`, beside the other body scripts and before `blazor.web.js`:

```razor
    <script src="js/shortcuts.js"></script>
```

In `src/SqlAgent.Host/Components/Layout/MainLayout.razor`, beside `<DialogHost />`:

```razor
    <KeyboardShortcuts />
```

In `src/SqlAgent.Host/Program.cs`, beside `DialogService`:

```csharp
builder.Services.AddScoped<ShortcutService>();
```

- [ ] **Step 5: Let `Menu` and `Modal` listen while open**

In `src/SqlAgent.Host/Components/Shared/Ui/Menu.razor`, add the injection and the subscription:

```razor
@implements IDisposable
@inject ShortcutService Shortcuts
```

```csharp
    private void Toggle()
    {
        _open = !_open;
        // Subscribed only while open: a sidebar of twenty rows would otherwise hold twenty live handlers
        // for the life of the circuit and run all of them on every Escape. Same discipline as the
        // drawer's conditional keydown handler.
        if (_open) Shortcuts.EscapePressed += OnGlobalEscape;
        else Shortcuts.EscapePressed -= OnGlobalEscape;
    }

    public void Close()
    {
        if (_open) Shortcuts.EscapePressed -= OnGlobalEscape;
        _open = false;
        StateHasChanged();
    }

    /// <summary>The document-level Escape. The local handler above still exists and still works
    /// everywhere it fires; this is what covers Safari, where a click leaves no focused element for the
    /// keypress to bubble from.</summary>
    private void OnGlobalEscape() => InvokeAsync(Close);

    public void Dispose() => Shortcuts.EscapePressed -= OnGlobalEscape;
```

In `src/SqlAgent.Host/Components/Shared/Ui/Modal.razor`, the same shape — a modal is open for its whole
lifetime, so it subscribes on initialize and unsubscribes on dispose:

```razor
@implements IDisposable
@inject ShortcutService Shortcuts
```

```csharp
    protected override void OnInitialized() => Shortcuts.EscapePressed += OnGlobalEscape;

    private void OnGlobalEscape() => InvokeAsync(Close);

    public void Dispose() => Shortcuts.EscapePressed -= OnGlobalEscape;
```

- [ ] **Step 6: Register the service in every fixture that renders one of them**

`Menu` and `Modal` now resolve `ShortcutService`, so a bUnit context that renders either without it
throws. Add `ctx.Services.AddScoped<ShortcutService>();` — or `_ctx.Services...`, matching each file's
own style — to the fixtures in: `UiInteractionTests`, `UserCardTests`, `ShellTests`
(`RegisterSidebarServices`), `ComposerTests`, `HistorySectionTests`, `ChatRowTests`,
`ProjectSectionTests` and `ChatPageTests`. Change nothing else in those files.

A missing registration failing loudly is the point: the alternative — resolving the service optionally so
absence is silent — would let a real deployment lose Escape with nothing to notice it.

- [ ] **Step 7: Run the tests to verify they pass**

Run: `dotnet test SqlAgent.slnx --configuration Release`
Expected: PASS. Run the whole suite here rather than a filter: this step's blast radius is every fixture
that renders a popover, and a filtered run would not show it.

- [ ] **Step 8: Commit**

```bash
git add src/SqlAgent.Host tests/SqlAgent.Tests
git commit -m "$(cat <<'COMMIT'
Add one document keydown listener, and let Escape reach Safari's menus

Ctrl/Cmd+K has to work wherever focus is, and Blazor only hears elements it
rendered — so a document-level listener is unavoidable. preventDefault goes with
it, or Chrome takes the shortcut for its address bar.

The same listener closes a defect Phase A carried: Safari does not focus a button
on a plain mouse click, so a menu opened by mouse has nothing for Escape to
bubble from and cannot be dismissed from the keyboard there. Menu and Modal keep
their local handlers, which work everywhere else, and subscribe to the service
only while open — a sidebar of twenty rows must not hold twenty live handlers.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
COMMIT
)"
```

---

### Task 7: The search modal

**Files:**
- Create: `src/SqlAgent.Host/Components/Shared/Chat/SearchDialog.razor` + `.razor.css`
- Modify: `src/SqlAgent.Host/Components/Layout/SidebarNav.razor` (the Search row and the subscription)
- Modify: `src/SqlAgent.Host/Components/Shared/Ui/Icon.razor` (`search`), `tests/SqlAgent.Tests/UiPrimitiveTests.cs` (its inventory)
- Modify: `src/SqlAgent.Host/Web/AppState.cs` (`RequestProjectExpanded`), `Components/Layout/ProjectSection.razor` (honour it)
- Test: `tests/SqlAgent.Tests/SearchDialogTests.cs`

**Interfaces:**
- Consumes: `SearchService` (Task 2), `ShortcutService` (Task 6), `DialogService`, `Modal`.
- Produces:
  - `<SearchDialog OnClose="EventCallback" />`
  - `AppState.ProjectToExpand: Guid?`, `AppState.RequestProjectExpanded(Guid id)`, raising `ChatsChanged`.
  - Icon name `search`.

**What opening a hit does, by kind:** a chat or a message hit navigates to `/chat/{id}` — a message hit
carries its chat's id, and the snippet in the row is what explains why it matched. A project hit asks the
sidebar to expand that project, because there is no project route to navigate to and a hit that does
nothing is worse than no hit. A database hit goes to `/connections`.

- [ ] **Step 1: Write the failing test**

Create `tests/SqlAgent.Tests/SearchDialogTests.cs`:

```csharp
using Bunit;
using Bunit.TestDoubles;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SqlAgent.Host.Components.Shared.Chat;
using SqlAgent.Host.Web;
using SqlAgent.Storage;

namespace SqlAgent.Tests;

public class SearchDialogTests : IDisposable
{
    private readonly SqliteConnection _conn = new("DataSource=:memory:");
    private readonly Bunit.TestContext _ctx = new();

    public SearchDialogTests()
    {
        _conn.Open();
        _ctx.Services.AddDbContext<SqlAgentDbContext>(o => o.UseSqlite(_conn));
        _ctx.Services.AddScoped<ChatService>();
        _ctx.Services.AddScoped<ProjectService>();
        _ctx.Services.AddScoped<SearchService>();
        _ctx.Services.AddScoped<ScopedRunner>();
        _ctx.Services.AddScoped<AppState>();
        _ctx.Services.AddScoped<ShortcutService>();
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        using var scope = _ctx.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<SqlAgentDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public void An_empty_box_shows_a_hint_rather_than_an_empty_list()
    {
        // A blank result area reads as "nothing found" when nothing has been asked yet.
        var dialog = _ctx.RenderComponent<SearchDialog>();

        Assert.Empty(dialog.FindAll("[data-testid=search-hit]"));
        Assert.Contains("Search", dialog.Markup);
    }

    [Fact]
    public async Task Typing_finds_a_chat_by_title_and_opening_it_navigates()
    {
        await SeedChatAsync("quarterly revenue", "body");
        var dialog = _ctx.RenderComponent<SearchDialog>();

        dialog.Find("input").Input("quarterly");
        await WaitForHitsAsync(dialog);

        var hit = dialog.FindAll("[data-testid=search-hit]").First();
        Assert.Contains("quarterly revenue", hit.TextContent);
        await hit.ClickAsync(new MouseEventArgs());

        Assert.Contains("/chat/", _ctx.Services.GetRequiredService<FakeNavigationManager>().Uri);
    }

    [Fact]
    public async Task A_message_match_shows_the_snippet_that_explains_it()
    {
        // The title is the first sixty characters of the first question, so a body match with no snippet
        // gives the user no idea why the chat is in the list.
        await SeedChatAsync("untitled", "the quick brown fox jumps over the lazy dog");
        var dialog = _ctx.RenderComponent<SearchDialog>();

        dialog.Find("input").Input("lazy");
        await WaitForHitsAsync(dialog);

        Assert.Contains("lazy", dialog.Markup);
    }

    [Fact]
    public async Task Nothing_found_says_so()
    {
        await SeedChatAsync("quarterly revenue", "body");
        var dialog = _ctx.RenderComponent<SearchDialog>();

        dialog.Find("input").Input("zzzz");
        await WaitForConditionAsync(() => dialog.Markup.Contains("No matches", StringComparison.Ordinal));

        Assert.Empty(dialog.FindAll("[data-testid=search-hit]"));
    }

    [Fact]
    public async Task The_arrow_keys_move_the_highlight_and_Enter_opens_it()
    {
        // The point of a command palette is that the hands never leave the keyboard.
        await SeedChatAsync("lazy one", "x");
        await SeedChatAsync("lazy two", "x");
        var dialog = _ctx.RenderComponent<SearchDialog>();
        dialog.Find("input").Input("lazy");
        await WaitForHitsAsync(dialog);

        Assert.Contains("highlighted", dialog.FindAll("[data-testid=search-hit]")[0].ClassName);

        await dialog.Find("input").KeyDownAsync(new KeyboardEventArgs { Key = "ArrowDown" });
        Assert.Contains("highlighted", dialog.FindAll("[data-testid=search-hit]")[1].ClassName);

        await dialog.Find("input").KeyDownAsync(new KeyboardEventArgs { Key = "ArrowUp" });
        Assert.Contains("highlighted", dialog.FindAll("[data-testid=search-hit]")[0].ClassName);

        await dialog.Find("input").KeyDownAsync(new KeyboardEventArgs { Key = "Enter" });
        Assert.Contains("/chat/", _ctx.Services.GetRequiredService<FakeNavigationManager>().Uri);
    }

    [Fact]
    public async Task The_highlight_does_not_run_off_either_end()
    {
        await SeedChatAsync("lazy one", "x");
        var dialog = _ctx.RenderComponent<SearchDialog>();
        dialog.Find("input").Input("lazy");
        await WaitForHitsAsync(dialog);

        await dialog.Find("input").KeyDownAsync(new KeyboardEventArgs { Key = "ArrowUp" });
        await dialog.Find("input").KeyDownAsync(new KeyboardEventArgs { Key = "ArrowDown" });
        await dialog.Find("input").KeyDownAsync(new KeyboardEventArgs { Key = "ArrowDown" });

        Assert.Contains("highlighted", dialog.FindAll("[data-testid=search-hit]")[0].ClassName);
    }

    [Fact]
    public async Task Opening_a_project_hit_asks_the_sidebar_to_expand_it()
    {
        // There is no project route to navigate to, and a hit that does nothing when clicked is worse
        // than no hit at all.
        Guid projectId;
        using (var scope = _ctx.Services.CreateScope())
            projectId = (await scope.ServiceProvider.GetRequiredService<ProjectService>()
                .CreateProjectAsync("quarterly")).Id!.Value;
        var dialog = _ctx.RenderComponent<SearchDialog>();
        dialog.Find("input").Input("quarterly");
        await WaitForHitsAsync(dialog);

        await dialog.FindAll("[data-testid=search-hit]").First().ClickAsync(new MouseEventArgs());

        Assert.Equal(projectId, _ctx.Services.GetRequiredService<AppState>().ProjectToExpand);
    }

    private async Task SeedChatAsync(string title, string body)
    {
        using var scope = _ctx.Services.CreateScope();
        var chats = scope.ServiceProvider.GetRequiredService<ChatService>();
        var id = await chats.CreateChatAsync(title);
        await chats.AppendMessageAsync(new ChatMessageInput(id, ChatRole.User, body, []));
    }

    private static Task WaitForHitsAsync(IRenderedComponent<SearchDialog> dialog) =>
        WaitForConditionAsync(() => dialog.FindAll("[data-testid=search-hit]").Count > 0);

    private static async Task WaitForConditionAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition(), "The dialog never reached the expected state.");
    }

    public void Dispose()
    {
        _ctx.Dispose();
        _conn.Dispose();
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/SqlAgent.Tests/SqlAgent.Tests.csproj --filter SearchDialogTests`
Expected: FAIL — compile error, `SearchDialog` does not exist.

- [ ] **Step 3: Add the glyph and the expand request**

In `Icon.razor`:

```csharp
        // Phase B2: the sidebar's Search row.
        ["search"] = ["M20 11 A7 7 0 1 1 6 11 A7 7 0 1 1 20 11", "M17.5 16.5 L21 20"],
```

Add `"search"` to the `rendered` array and the `[InlineData]` list in `UiPrimitiveTests`.

In `src/SqlAgent.Host/Web/AppState.cs`:

```csharp
    /// <summary>A project the sidebar should open, set by a search hit. There is no project route to
    /// navigate to, so this is how a hit reaches a section that is not on the navigation path.</summary>
    public Guid? ProjectToExpand { get; private set; }

    public void RequestProjectExpanded(Guid projectId)
    {
        ProjectToExpand = projectId;
        ChatsChanged?.Invoke();
    }

    /// <summary>Reads the request and clears it, so re-rendering the sidebar for an unrelated reason does
    /// not keep re-opening a project the user has since collapsed.</summary>
    public Guid? TakeProjectToExpand()
    {
        var id = ProjectToExpand;
        ProjectToExpand = null;
        return id;
    }
```

In `ProjectSection.ReloadAsync`, before loading the expanded projects' chats:

```csharp
        if (State.TakeProjectToExpand() is { } requested && _projects.Any(p => p.Id == requested))
            _expanded.Add(requested);
```

- [ ] **Step 4: Write `SearchDialog`**

Create `src/SqlAgent.Host/Components/Shared/Chat/SearchDialog.razor`:

```razor
@inject ScopedRunner Runner
@inject AppState State
@inject NavigationManager Nav

<Modal Title="Search" OnClose="OnClose">
    <ChildContent>
        @* Enter, the arrows and Escape are handled here rather than by a document listener: focus is in
           this box for the dialog's whole life, so Blazor hears them without any interop. *@
        <input class="search-input" placeholder="Search chats, projects and databases"
               value="@_term" @oninput="OnTermChanged" @onkeydown="OnKeyDown" autofocus />

        @if (_term.Length == 0)
        {
            <p class="muted search-hint">Search your chats, their messages, your projects and your databases.</p>
        }
        else if (_hits.Count == 0)
        {
            <p class="muted search-hint">No matches.</p>
        }
        else
        {
            @foreach (var group in _hits.GroupBy(h => h.Kind))
            {
                <p class="search-heading">@LabelOf(group.Key)</p>
                @foreach (var hit in group)
                {
                    var index = _hits.IndexOf(hit);
                    <button type="button" data-testid="search-hit"
                            class="ghost search-hit @(index == _highlighted ? "highlighted" : "")"
                            @onclick="() => OpenAsync(hit)">
                        <span class="search-hit-label truncate">@hit.Label</span>
                        @if (hit.Snippet is { Length: > 0 } snippet)
                        {
                            <span class="search-hit-snippet truncate">@snippet</span>
                        }
                    </button>
                }
            }
        }
    </ChildContent>
</Modal>

@code {
    [Parameter] public EventCallback OnClose { get; set; }

    private string _term = "";
    private List<SearchHit> _hits = [];
    private int _highlighted;

    private async Task OnTermChanged(ChangeEventArgs e)
    {
        _term = e.Value?.ToString() ?? "";
        // Queried on every keystroke, deliberately: the store is a local file and the corpus is one
        // person's history, so an artificial delay would only add drag. SearchService returns early on a
        // blank term, so clearing the box costs no query at all.
        _hits = (await Runner.RunAsync<SearchService, IReadOnlyList<SearchHit>>(
            s => s.SearchAsync(_term))).ToList();
        _highlighted = 0;
    }

    private async Task OnKeyDown(KeyboardEventArgs e)
    {
        switch (e.Key)
        {
            case "ArrowDown":
                // Clamped rather than wrapped: a list that jumps from the last row to the first loses the
                // user's place in a list they are reading top to bottom.
                _highlighted = Math.Min(_highlighted + 1, Math.Max(_hits.Count - 1, 0));
                break;
            case "ArrowUp":
                _highlighted = Math.Max(_highlighted - 1, 0);
                break;
            case "Enter" when _hits.Count > 0:
                await OpenAsync(_hits[_highlighted]);
                break;
            case "Escape":
                await OnClose.InvokeAsync();
                break;
        }
    }

    private async Task OpenAsync(SearchHit hit)
    {
        await OnClose.InvokeAsync();
        switch (hit.Kind)
        {
            case SearchHitKind.Chat:
            case SearchHitKind.Message:
                // A message hit carries its chat's id: the chat is what opens, and the snippet in the row
                // is what explained why it matched.
                Nav.NavigateTo($"/chat/{hit.TargetId}");
                break;
            case SearchHitKind.Project:
                State.RequestProjectExpanded(hit.TargetId);
                break;
            case SearchHitKind.Database:
                Nav.NavigateTo("/connections");
                break;
        }
    }

    private static string LabelOf(SearchHitKind kind) => kind switch
    {
        SearchHitKind.Chat => "Chats",
        SearchHitKind.Message => "In messages",
        SearchHitKind.Project => "Projects",
        _ => "Databases",
    };
}
```

Create `src/SqlAgent.Host/Components/Shared/Chat/SearchDialog.razor.css`:

```css
.search-input {
  width: 100%;
  margin-bottom: var(--space-3);
}
.search-hint { font-size: var(--text-sm); padding: var(--space-3) 0; }

.search-heading {
  margin-top: var(--space-3);
  color: var(--text-100);
  font-size: var(--text-xs);
  font-weight: 500;
}

.search-hit {
  display: flex;
  flex-direction: column;
  align-items: flex-start;
  width: 100%;
  gap: 2px;
  padding: var(--space-2);
  text-align: left;
  border-radius: var(--radius-control);
}
.search-hit:hover, .search-hit.highlighted { background: var(--background-soft-100); }
.search-hit-label { color: var(--title-50); }
.search-hit-snippet { color: var(--text-100); font-size: var(--text-xs); }
```

- [ ] **Step 5: Add the Search row and the shortcut subscription**

`src/SqlAgent.Host/Components/Layout/SidebarNav.razor` gains a row and owns opening the dialog, so the
click and the shortcut go through one path:

```razor
@implements IDisposable
@inject DialogService Dialogs
@inject ShortcutService Shortcuts

<nav class="sidebar-nav">
    <NavLink class="nav-row" href="/" Match="NavLinkMatch.All">
        <Icon Name="plus" Size="18" />
        <span class="nav-label">New chat</span>
    </NavLink>
    @* A button rather than a NavLink: search is a dialog, not a route, and a link that navigates nowhere
       is a lie in the markup. *@
    <button type="button" class="nav-row" data-testid="nav-search" @onclick="ShowSearch">
        <Icon Name="search" Size="18" />
        <span class="nav-label">Search</span>
    </button>
    ... the SQL, Connections and Settings rows unchanged ...
</nav>

@code {
    [Parameter] public bool Collapsed { get; set; }

    protected override void OnInitialized() => Shortcuts.SearchRequested += OnSearchRequested;

    private void OnSearchRequested() => InvokeAsync(() => { ShowSearch(); StateHasChanged(); });

    private void ShowSearch() => Dialogs.Show(@<SearchDialog OnClose="Dialogs.Close" />);

    public void Dispose() => Shortcuts.SearchRequested -= OnSearchRequested;
}
```

`SidebarNav.razor.css` needs the button to match the links it sits among; add to its existing
`.nav-row` block or beside it:

```css
/* The Search row is a <button>, not an <a>, so it needs the border and background reset the base button
   style applies and the nav rows do not want. */
.sidebar-nav ::deep button.nav-row {
  width: 100%;
  border: none;
  background: none;
}
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test SqlAgent.slnx --configuration Release`
Expected: PASS. Whole suite again: `SidebarNav` now resolves two services, so `ShellTests` exercises the
change too.

- [ ] **Step 7: Commit**

```bash
git add src/SqlAgent.Host tests/SqlAgent.Tests
git commit -m "$(cat <<'COMMIT'
Add the search modal, on a row and on Ctrl/Cmd+K

One path for both: the nav row and the shortcut call the same method, so there is
no second way for them to disagree. The row is a button rather than a NavLink,
because search is a dialog and a link that navigates nowhere is a lie in the
markup.

Arrow keys clamp instead of wrapping — a list that jumps from the last row to the
first loses the reader's place. A project hit asks the sidebar to expand that
project rather than navigating, because there is no project route and a hit that
does nothing when clicked is worse than no hit.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
COMMIT
)"
```

---

### Task 8: The three remaining carried debts

**Files:**
- Modify: `tests/SqlAgent.Tests/ChatPageTests.cs` (the test that cannot fail; the alias)
- Rename: `src/SqlAgent.Host/Components/Pages/Chat.razor` → `ChatPage.razor` (+ its `.razor.css`)
- Modify: `tests/SqlAgent.Tests/RestyleRegressionTests.cs`, `tests/SqlAgent.Tests/UiPrimitiveTests.cs`

**Interfaces:**
- Consumes: nothing new.
- Produces: no runtime API. The page's routes (`/` and `/chat/{Id:guid}`) are unchanged — a Blazor page's
  component name never appears in a URL.

- [ ] **Step 1: Make the sidebar-notification test able to fail**

`ChatPageTests.A_dropped_send_still_tells_the_sidebar_a_chat_was_created` passes with the production fix
reverted: the `SetParametersAndRender` that makes the send stale itself fires `ChatsChanged` through
`SetActiveChat`, so `notified` is already above zero before the gateway is released. Snapshot the count
immediately before the release and assert it grew:

```csharp
        // Snapshot here, not at the top: re-parameterizing to chat B already fired ChatsChanged through
        // SetActiveChat, so a test that asserted notified > 0 would pass with the production fix
        // reverted — the mechanism that makes the send stale is the same one that fires the event.
        var before = notified;
        _gateway.Release(LlmSqlResponse.Generated("SELECT 1"));
        await send;

        Assert.True(notified > before);
```

Prove it: revert the hoist in `Chat.razor` (move `State.NotifyChatsChanged()` back below the drop check),
watch this test go red, restore the hoist, watch it go green. Report both runs.

- [ ] **Step 2: Rename the page**

`git mv src/SqlAgent.Host/Components/Pages/Chat.razor src/SqlAgent.Host/Components/Pages/ChatPage.razor`
and the same for `Chat.razor.css` → `ChatPage.razor.css`. Both `@page` directives stay exactly as they
are.

Then delete the alias from `tests/SqlAgent.Tests/ChatPageTests.cs` — the `using ChatPage = …` line and its
comment — and add `using SqlAgent.Host.Components.Pages;` instead. Every `ChatPage` reference in the file
now resolves to the real type name. `SqlAgent.Storage.Chat` (the entity) no longer collides with
anything.

Search the tree for other references before you finish: `grep -rn "Pages.Chat\b" src tests` should come
back empty.

- [ ] **Step 3: Make `RestyleRegressionTests` check scope, not presence**

`Every_class_the_existing_markup_uses_is_styled_somewhere` concatenates every stylesheet before
searching, so a rule in the *wrong* sheet still passes — precisely the failure that hit Phase A twice.
Blazor compiles each component's scoped CSS to `obj/<config>/<tfm>/scopedcss/Components/**/X.razor.rz.scp.css`,
one file per component. That is the artifact that makes scope checkable: a rule for a class lives in the
compiled file of the component whose stylesheet declared it, so "the right sheet" becomes a file
comparison instead of a substring search over a concatenation.

Replace `Every_class_the_existing_markup_uses_is_styled_somewhere` with:

```csharp
    [Fact]
    public void Every_class_the_markup_uses_is_styled_where_it_can_reach_it()
    {
        // The old version concatenated every stylesheet and searched the result, so a rule that had
        // drifted into the wrong component's sheet still passed — precisely the failure that hit Phase A
        // twice. Blazor's scoped CSS is per component: a rule in Foo.razor.css compiles to
        // scopedcss/Components/.../Foo.razor.rz.scp.css and matches only elements Foo itself rendered.
        // So the honest question is not "is this class styled somewhere" but "is it styled somewhere
        // that can reach the markup using it" — this component's own scoped sheet, or a global rule.
        var componentsRoot = Path.GetDirectoryName(RepoPaths.Find("src/SqlAgent.Host/Components/App.razor"))!;
        var global = StripComments(File.ReadAllText(RepoPaths.Find("src/SqlAgent.Host/wwwroot/css/app.css")));

        var unreachable = new List<string>();
        foreach (var razor in Directory.EnumerateFiles(componentsRoot, "*.razor", SearchOption.AllDirectories))
        {
            var markup = File.ReadAllText(razor);
            var scoped = CompiledScopedCssFor(razor);
            foreach (var className in ClassesUsedByExistingMarkup.Where(c => UsesClass(markup, c)))
                if (!IsClassStyled(global, className) && !IsClassStyled(scoped, className))
                    unreachable.Add($"{Path.GetFileName(razor)} uses .{className}");
        }

        Assert.Empty(unreachable);
    }

    /// <summary>The build output for one component's scoped stylesheet, or empty when the component has
    /// none. Missing output for the whole project is a failure rather than an empty pass: a guard that
    /// disappears when its artifact is absent is the same defect in a new place.</summary>
    private static string CompiledScopedCssFor(string razorPath)
    {
        var hostRoot = Path.GetDirectoryName(RepoPaths.Find("src/SqlAgent.Host/Components/App.razor"))!;
        hostRoot = Path.GetDirectoryName(hostRoot)!;
        var scopedRoots = Directory.Exists(Path.Combine(hostRoot, "obj"))
            ? Directory.GetDirectories(Path.Combine(hostRoot, "obj"), "scopedcss", SearchOption.AllDirectories)
            : [];
        Assert.NotEmpty(scopedRoots.Where(r => Directory.Exists(Path.Combine(r, "Components"))));

        var relative = Path.GetRelativePath(hostRoot, razorPath) + ".rz.scp.css";
        foreach (var root in scopedRoots)
        {
            var candidate = Path.Combine(root, relative);
            if (File.Exists(candidate)) return StripComments(File.ReadAllText(candidate));
        }
        return "";
    }

    /// <summary>Whether this component's own markup carries the class, so the question is asked only of
    /// the components that actually use it.</summary>
    private static bool UsesClass(string markup, string className) =>
        Regex.IsMatch(markup, $@"class\s*=\s*""[^""]*\b{Regex.Escape(className)}\b");

    private static string StripComments(string css) =>
        Regex.Replace(css, @"/\*.*?\*/", "", RegexOptions.Singleline);
```

`IsClassStyled` already exists in this file and is reused unchanged. Keep `ClassesUsedByExistingMarkup`
and its comment; the array is still the list of classes worth guarding.

- [ ] **Step 4: Make the icon inventory test scan the markup**

`No_icon_ships_that_nothing_renders` compares `Icon.Names` against a hardcoded array, so it is a
change-detector wearing a policy's name: adding a glyph and adding its name to the array satisfies it
without anything rendering the glyph. Replace the array with a scan of the markup:

```csharp
    [Fact]
    public void No_icon_ships_that_nothing_renders()
    {
        // Each phase ships only the glyphs it draws, so an unused glyph never sits in the set waiting
        // for a caller a later phase might rename or never write.
        //
        // This used to compare Icon.Names against a hardcoded list, which made it a change-detector
        // wearing a policy's name: adding a glyph and adding its name to the list satisfied it while
        // nothing rendered the glyph. It now reads the markup, so the only way to pass is to render it.
        // Every reference is a literal `Name="…"` — Task 5's project chevron is two elements under an
        // @if rather than one with a ternary, precisely so this scan can be a plain regex instead of an
        // expression parser with bugs of its own.
        var componentsRoot = Path.GetDirectoryName(RepoPaths.Find("src/SqlAgent.Host/Components/App.razor"))!;

        var rendered = Directory
            .EnumerateFiles(componentsRoot, "*.razor", SearchOption.AllDirectories)
            .SelectMany(f => Regex.Matches(File.ReadAllText(f), @"<Icon\b[^>]*?\bName\s*=\s*""([a-z0-9-]+)"""))
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(rendered);
        Assert.Equal(
            rendered.OrderBy(n => n, StringComparer.Ordinal),
            Icon.Names.OrderBy(n => n, StringComparer.Ordinal));
    }
```

`Regex` needs `using System.Text.RegularExpressions;` at the top of the file if it is not there already.

`The_icons_the_shell_needs_all_exist` stays exactly as it is: it pins the names the shell references by
string, which is the opposite direction and still worth having.

- [ ] **Step 5: Run the whole suite**

Run: `dotnet build SqlAgent.slnx --configuration Release && dotnet test SqlAgent.slnx --configuration Release`
Expected: PASS. Build first: Step 3's test now reads a build artifact.

- [ ] **Step 6: Commit**

```bash
git add src tests
git commit -m "$(cat <<'COMMIT'
Close three carried debts: a toothless test, a name collision, two false guards

The sidebar-notification test passed with its own production fix reverted,
because the re-parameterization that makes the send stale is the same thing that
fires the event it was asserting on. It now snapshots the count immediately
before the release.

The chat page is renamed to ChatPage so it stops colliding with the Chat entity;
routes are untouched, since a Blazor page's component name never appears in a
URL, and the test alias goes away while two files carry it rather than twenty.

Both Phase A tests that did not test what their names claim now do: the restyle
guard reads the compiled scoped-CSS bundle so a rule in the wrong sheet fails,
and the icon inventory scans the markup for the glyph names actually rendered
instead of a hardcoded array kept in step by hand.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
COMMIT
)"
```

---

### Task 9: Documentation and phase verification

**Files:**
- Modify: `docs/web-ui.md`, `README.md`

- [ ] **Step 1: Document projects and search in `docs/web-ui.md`**

Under the existing "Chats, and what is kept" section, add:

```markdown
## Projects

A chat belongs to at most one project, and a chat in a project leaves the history list — each
conversation is in exactly one place in the sidebar. Move one with **Move to project** in its `⋮` menu;
"No project" in that dialog moves it back.

Project names are unique and case-insensitive: `Quarterly` and `quarterly` are the same name, and the
dialog says so rather than closing and doing nothing.

Deleting a project asks what to do with its chats — return them to your history, or delete them with it.
There is no silent cascade: it is the only action here that can destroy a conversation.

## Search

`Ctrl`/`Cmd`+`K` from anywhere, or the Search row in the sidebar. It searches chat titles, message text,
project names and database names, grouped by kind, newest first within each group. Arrow keys move,
Enter opens, Escape closes.

A message match shows the text around it, and opens the chat at the top — matches are not scrolled to.
A project match opens that project in the sidebar; a database match goes to Connections.

Wildcards are searched for literally: `50%` finds a percent sign, and `a_b` does not match `axb`.
```

- [ ] **Step 2: Extend the manual regression checklist**

Add these rows to the checklist table in `docs/web-ui.md`:

```markdown
| Press `Ctrl`/`Cmd`+`K` with focus in the composer, then with nothing focused | The search modal opens both times |
| Press `Ctrl`/`Cmd`+`K` in Chrome and Firefox | The browser's own shortcut does not fire — no address bar, no find bar |
| In Safari, open the user menu with the mouse and press Escape | The menu closes |
| Open a project, move a chat into it, reload | The chat is under the project and not in the history list |
| Delete a project holding a chat, choosing "keep the chats" | The chat is back in the history list |
| Search for a term that appears only in a message body | The result shows the surrounding text and opens the chat |
```

- [ ] **Step 3: Update the README**

Extend the Web UI sentence to name what is new:

```markdown
Details on the shell, the screens, chat persistence, projects and search, the token, and the manual
regression checklist for the parts automated tests can't reach are in [`docs/web-ui.md`](docs/web-ui.md).
```

- [ ] **Step 4: Full verification**

```bash
dotnet build SqlAgent.slnx --configuration Release
dotnet test SqlAgent.slnx --configuration Release
```

Expected: build clean with no new warnings, all tests pass.

The browser rows above cannot be walked by an agent — there is no one at the screen. Write them and say
plainly in the report which checks were left for a human, rather than reporting them as done.

- [ ] **Step 5: Commit**

```bash
git add docs/web-ui.md README.md
git commit -m "$(cat <<'COMMIT'
Document projects, search, and the checks only a browser can make

Records that a chat lives in exactly one place in the sidebar, that project names
are case-insensitive and unique, that deleting a project asks about its chats,
and that wildcards in a search term are searched for literally. Adds the six
manual checks bUnit cannot reach — the global shortcut with and without focus in
a field, the browser's own Ctrl+K losing to ours, and Safari's Escape.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
COMMIT
)"
```

---

## Phase B2 Definition of Done

- [ ] `dotnet build SqlAgent.slnx --configuration Release` is clean and `dotnet test SqlAgent.slnx --configuration Release` is green, every pre-existing test included.
- [ ] A chat moved into a project appears under it and no longer in history; moving it back restores it.
- [ ] Deleting a project offers both outcomes and performs the one chosen.
- [ ] A project name cannot be taken twice, differs case-insensitively, and the dialog says so rather than failing silently.
- [ ] Search finds a chat by title, a chat by message text with a snippet, a project by name and a database by name; `50%` searches for a percent sign.
- [ ] `Ctrl`/`Cmd`+`K` opens search wherever focus is, and the browser's own shortcut does not fire with it.
- [ ] No component stylesheet contains a literal color.
- [ ] The four carried debts are closed: Safari's Escape, the test that could not fail, the `Chat`/`Chat` collision, and the two Phase A tests that did not test their names.
- [ ] `docs/web-ui.md` documents projects, search, and the new manual checks.

## Self-Review Notes

Checked against the spec section by section:

| Spec requirement | Task |
|---|---|
| `Project` entity, `Chat.ProjectId`, FK with `Restrict`, one migration | 1 |
| No `Description` column | 1 (absent by construction) |
| `ProjectService`: list with counts, create, rename, delete both ways, move | 1 |
| Writes return an outcome rather than a bool | 1 |
| `ListHistoryAsync` excludes chats in a project | 1 |
| `SearchService`: kinds, escaping, 50-per-kind cap, per-chat collapse, recency order | 2 |
| Blank term issues no query | 2 |
| `NameDialog` supersedes `ChatRenameDialog` | 3 |
| `ChatRow` extracted, used by both sections | 4 |
| Project section: heading, add, folder rows with counts, collapsible, chats nested | 5 |
| Move to project via a dialog, not a submenu | 5 |
| Delete asks keep-or-delete; taken name reported beside the field | 5 |
| Expansion state not persisted | 5 |
| Two empty states, deliberately different | 5 (project section), B1 (history) |
| `ShortcutService`, `shortcuts.js`, `KeyboardShortcuts` | 6 |
| `Menu`/`Modal` subscribe while open; Safari debt closed | 6 |
| Search modal: input, grouped hits, arrows, Enter, Escape | 7 |
| `Ctrl`/`Cmd`+`K` with `preventDefault` | 6 (listener), 7 (what it opens) |
| Snippet in the row; chat opens at the top | 2 (snippet), 7 (navigation) |
| Debts: hollow test, `ChatPage` rename, two Phase A tests | 8 |
| Docs and manual checklist | 9 |

**Three decisions this plan makes that the spec did not.**

1. **`ChatRow` owns its actions rather than reporting them upward.** The spec says the row is extracted
   because two sections render it; it does not say where the rename/delete/move logic goes. Leaving the
   row presentational would have duplicated roughly thirty lines of dialog-and-write code in both
   sections — the "verbatim duplication of a logic block" a reviewer is required to raise as Important.
   The row therefore resolves its own services and announces through `AppState`, which both sections
   already listen to.
2. **A project search hit expands the project in the sidebar** through a new `AppState` request, because
   there is no project route and the spec did not say what opening one does. A hit that does nothing when
   clicked is worse than no hit.
3. **`ShortcutService` is resolved normally, not optionally**, which is why Task 6 touches eight test
   fixtures. Resolving it optionally would have kept those files untouched at the cost of letting a real
   deployment lose Escape and the shortcut with nothing to notice it.

**Types used across tasks, defined once:** `Project`, `ProjectSummary`, `ProjectWriteOutcome`,
`ProjectWriteResult`, `ProjectDeleteMode`, `ProjectService.*`, `SearchHitKind`, `SearchHit`,
`SearchService.SearchAsync`, `ChatRow.{Chat,Active,CurrentProjectId}`,
`NameDialog.{Title,Label,InitialValue,ConfirmLabel,Error,OnSave,OnCancel}`,
`MoveToProjectDialog.{Chat,Projects,CurrentProjectId,OnPick,OnCancel}`,
`ProjectDeleteDialog.{Project,OnConfirm,OnCancel}`,
`ShortcutService.{EscapePressed,SearchRequested,RaiseEscape,RaiseSearch}`,
`AppState.{ProjectToExpand,RequestProjectExpanded,TakeProjectToExpand}`,
`window.sqlAgentShortcuts.{bind,unbind}`.

**Still carried after B2**, from B1's list: the two open decisions for the maintainer (copying the store
before applying pending migrations, and walking B1's manual checklist), the `IsSequenceCollision` message
match, the redundant attachment index, the composer's per-keystroke round trip, `DialogService.Show`
replacing without a signal, the schema fingerprint's blind spots, and the empty-chat leak on a cancelled
first turn.
