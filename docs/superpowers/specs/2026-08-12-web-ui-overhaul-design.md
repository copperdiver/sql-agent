# Web UI overhaul: AI-chat shell, database configuration, embedded SQL components

- **Status:** approved, ready for implementation planning
- **Date:** 2026-08-12
- **Scope:** phases A, B1, B2, C–E (see [Phasing and priority](#phasing-and-priority)).
  Phase B was split in two during B1's design; B1 has its own spec,
  [2026-08-12 phase B1](2026-08-12-web-ui-phase-b1-chat-persistence-design.md).
- **Visual reference:** <https://aichat.demos.tailgrids.com/>, captured in
  `docs/superpowers/reference/tg-*.png`

## Context

The web UI that replaced the WPF client
([2026-08-11 spec](2026-08-11-local-web-ui-design.md)) shipped functionally complete
and visually unfinished. Concretely, as of commit `5ef8676`:

- **There is no application CSS at all.** `Components/App.razor` links
  `lib/codemirror/codemirror.min.css` and nothing else. Every screen renders as
  unstyled browser-default HTML. Components already carry class names
  (`rail`, `tree`, `outcome`, `grid-scroll`, `meta`) that no stylesheet defines.
- **Chat is not persisted.** `Workspace.razor` holds the transcript in a
  `List<TranscriptEntry>` field. Reloading the page loses the conversation. There
  are no projects, no history, no search.
- **Navigation is two links** in a `<header>`: Workspace and Connections.
- **The schema rail is the only access-control surface**, offering one checkbox
  per table. `TablePolicy` already has `CanRead`/`CanWrite` columns that nothing
  reads or writes.
- **The schema model describes base tables only.** `DatabaseSchema` is
  `record DatabaseSchema(IReadOnlyList<SchemaTable> Tables)` — no views, no
  routines.
- **All DDL is denied unconditionally.** `SqlStatementKind.Other` covers
  DDL/EXEC/TRUNCATE and is documented as "never supported by the agent in v1
  (fail closed)".
- **No LLM provider is wired.** `UnavailableLlmSqlGateway` throws, so
  `ask_database` always resolves to `llm_not_configured`. This is deliberate and
  unchanged by this work: model calls will go to a SQL Agent web service hosting
  several models, built after the Web UI. See [Non-goals](#non-goals).

## Goals

Bring the web UI to the standard of the reference AI-chat interface, and give the
agent's access control a real configuration surface:

1. A styled application shell with light/dark/system themes, a sidebar carrying
   user info, New Chat, Search, Databases, Projects, and History.
2. Persisted chats, grouped into projects and day-bucketed history, searchable.
3. A database configuration page: connection setup, then per-object access levels
   and explicit permissions for structural change.
4. Chat-embedded components purpose-built for SQL work: executable SQL blocks, a
   real data table, and an ER diagram.
5. File attachments behind a pluggable storage provider that returns URLs.

### Where the chat fits

SQL Agent is a local service that mediates database access for language models:
it holds connection strings in a secret store, describes schemas, enforces
per-object visibility and read/write policy, and executes validated SQL. MCP is
one way to reach that mediation — the canonical one for IDE hosts (ADR-0001).

The built-in chat is the **alternative** way to reach the same mediation, for
people who are not working in an editor. It is not a second engine with second
rules: it goes through the same `QueryExecutionService`, the same policy, and the
same audit trail, and it addresses databases by the **name** given in connection
settings, exactly as the MCP tools do. Neither surface is a place where a person
pastes a connection string or a page of production rows.

Databases reach a conversation as **context attached to a message** — zero, one,
or several, added from the composer's attachment menu, the same menu that Phase E
adds files to.

## Non-goals

| Out of scope | Why |
|---|---|
| Wiring a real LLM provider | Model calls will go to a SQL Agent web service hosting several models, developed after this Web UI work. The selector, composer, and attachment seam are built to accept it; none of them require it to exist. Chat continues to return `llm_not_configured` throughout A–E. |
| Locking in the shape of `ILlmSqlGateway` | Today's seam takes one question against one schema and returns one SQL string. A conversation carrying several databases, standing as the alternative to MCP, more likely wants a tool-calling loop over `list_databases` / `describe_schema` / `query_database`. That is the web-service phase's design decision, not this one's; nothing here should be read as fixing the current shape. |
| Procedures, functions, sequences in the object browser | Phase C ships tables and views. Routine catalogs diverge sharply between SQL Server and Postgres, and granting a routine to the agent means allowing `EXEC`, which stays denied. Deferred to its own phase. |
| View definitions (`CREATE VIEW` bodies) | Column lists are what query generation needs. Definitions are unbounded text headed for an LLM prompt; including them needs a budget story that belongs with the routines work. |
| A remote file-storage provider (S3, Azure Blob) | The seam and one local provider ship here. A remote provider is a separate project, like the database providers. |
| Streaming assistant responses | There is no provider to stream from. A pending indicator covers the in-flight case. |
| Real logos for SQL Server / PostgreSQL | Vendoring third-party brand marks is a trademark question this work does not need to answer. Provider glyphs are a database cylinder tinted per provider with provider initials. |
| Multi-user accounts, sign-in, sign-out | The host is loopback-only and single-user, authenticated by a launch token. See [User identity](#user-identity). |
| Remote access, TLS, portable secret store | Still phases 2 and 3 of the previous spec's roadmap. Untouched here. |

## Decisions taken before design

Recorded because each closed off a plausible alternative:

| Decision | Alternative rejected | Why |
|---|---|---|
| Hand-written CSS with the reference's token palette | Adding a Tailwind v4 build | Node would become a build prerequisite for a solution that is 100% .NET, and for CI. The tokens are what carry the look; the utilities are replaceable. |
| Read-only OS account in the user card | Editable local profile | Nothing else in the app has a notion of a person. Showing `Environment.UserName` is true without inventing a user model. |
| Model selector renders configured providers, empty state when none | Static decorative list | A dropdown listing models the app cannot call misrepresents what it can do. |
| Tables and views now | All object types now | See non-goals. |
| Projects group chats only | Projects pinning a default database or instructions | Matches the reference. Databases are attached per message, not owned by a chat or a project. |
| Databases attach to a message, from the composer's attachment menu | A single `Chat.DatabaseConnectionId` with a pill in the chat header | A conversation may involve no database, one, or several, and the transcript has to stay honest about which ones each question was asked against. The chips carry over between messages so nothing has to be re-attached. |
| DDL permissions are enforced, with confirm-before-run | Configure-now-enforce-later; or enforce silently | A toggle that does nothing is a lie in the UI. Enforcing without confirmation lets a model mutate structure unattended. |
| Mermaid, vendored locally | Hand-rolled SVG diagram | Layout, cardinality, and theming for free. The host is offline, so it is vendored, not CDN-loaded. |
| Attachments go through `IFileStorageProvider`, which returns a URL | Storing bytes in SQLite and inlining text into the prompt | The provider seam matches ADR-0005's existing boundary and leaves room for a cloud-reachable store later. |
| The SQL editor survives in both places: a scratchpad panel on the chat page (D) and a permanent `/sql` page (B1) | Chat-only | Nothing that works today is lost and the tab split disappears, while long queries and wide results still get a full screen. Both render the same components. |

## Architecture

All UI work stays in `SqlAgent.Host`. Core, Storage, and the two providers change
only where Phase C and E require (schema extraction, policy, file storage seam).
No new project is added.

### Target file layout

```
src/SqlAgent.Host/
  Components/
    App.razor                     theme bootstrap script, font + stylesheet links
    Routes.razor                  unchanged
    Layout/
      MainLayout.razor            sidebar + main card; keeps WorkArea
      Sidebar.razor               composes the sections below
      SidebarHeader.razor         logo + collapse toggle
      SidebarNav.razor            New Chat, SQL, Search
      DatabaseSection.razor       collapsible database list + Add database   (C)
      ProjectSection.razor        collapsible project list + Add project     (B2)
      HistorySection.razor        day-grouped chat list                      (B1)
      UserCard.razor              OS account + menu (Settings, Theme, About)
      SchemaRail.razor            restyled (A), DELETED in C
    Pages/
      Chat.razor                  "/" (new chat) and "/chat/{id}"            (B1)
      Database.razor              "/database/{id?}"                          (C)
      Settings.razor              "/settings"                                (A)
      Workspace.razor             restyled (A), tab strip dropped and moved
                                  to "/sql" (B1), KEPT
      Connections.razor           restyled (A), DELETED in C
    Shared/
      Ui/                         Icon, Menu, Modal, Segmented, Toggle,
                                  Badge, Spinner, ConfirmDialog, EmptyState
      Chat/                       MessageList, UserMessage, AssistantMessage,
                                  Composer, AttachmentMenu, AttachmentChips  (B1)
                                  SqlBlock, DataTable, ErDiagram, ScratchPad (D)
      OutcomeMessage.razor        kept as-is
      WorkArea.razor              kept as-is
      SqlEditor.razor             kept as-is (used by ScratchPad)
      ResultGrid.razor            restyled (A), DELETED in D
      ChatOutcome.razor           restyled (A), DELETED in D
  Web/
    AppState.cs                   extended (see State)
    DialogService.cs              new, Phase B1 — dialogs render from
                                  MainLayout, outside the sidebar's transform
    ResultExport.cs               kept as-is
    SqlHighlighter.cs             new, Phase D
    MermaidSource.cs              new, Phase D
    FileEndpoints.cs              new, Phase E
  wwwroot/
    css/app.css                   tokens, reset, layout, components
    fonts/                        DM Sans woff2 + OFL license
    js/theme.js                   pre-paint theme application
    js/download.js                kept as-is
    js/sql-editor.js              kept as-is
    lib/codemirror/               kept as-is
    lib/mermaid/                  new, Phase D (MIT license file included)
```

### State

`AppState` (circuit-scoped, per browser tab) is extended from "which connection
is selected" to also hold the active chat and the SQL handed from an answer to
the editor, with one event per concern. There is no "active database": databases
belong to messages, and the composer's chips are the page's own state until a
message is sent. This preserves the existing pattern
and the reason for it: Blazor siblings do not re-render each other, so the
sidebar sections and the page subscribe to the same state object. The existing
`Changed` / `ConnectionsChanged` events and their documented rationale stay.

Theme is **not** in `AppState`: it is a per-browser preference in
`localStorage`, applied before the circuit connects (see below).

### Component retirement order

No phase may leave the app without a working path to something it had before, so
components are retired only once their replacement exists:

| Component | Restyled | Retired | Because |
|---|---|---|---|
| `Connections.razor` | A | C | Connection management must stay reachable until `Database.razor` replaces it. |
| `SchemaRail.razor` | A | C | It is the only visibility surface until the config page's Objects panel exists. |
| `Workspace.razor` | A | never | `Chat.razor` takes `/` in B1, so Workspace drops its tab strip and moves to `/sql`, where it stays. D adds `ScratchPad` beside it, built from the same components, rather than replacing it. |
| `ChatOutcome.razor` | A | D | Phase B1's `AssistantMessage` delegates to it; D replaces that with `SqlBlock` + `DataTable`. |
| `ResultGrid.razor` | A | D | Used by Workspace and by B's assistant messages until `DataTable` exists. |

## Phase A — Design system and application shell

Priority **P0**. No other phase renders correctly without it.

### Tokens

`wwwroot/css/app.css` opens with a `:root` block and a `:root.dark` override
using identical names, so every component rule is authored once against
`var(--…)`. Values are taken from the reference's own stylesheet:

```css
:root{
  --background-50:#fff; --background-100:#fff;
  --background-soft-50:#f9fafb; --background-soft-100:#f3f4f6;
  --background-soft-200:#f3f4f6; --background-soft-400:#e5e7eb;
  --title-50:#1f2937; --text-50:#374151; --text-100:#6b7280; --text-200:#4b5563;
  --base-50:#f3f4f6; --base-100:#e5e7eb; --base-200:#d1d5db;
  --primary-50:#eff3ff; --primary-300:#91aeff;
  --primary-400:#5e84fc; --primary-500:#3758f9;
}
:root.dark{
  --background-50:#030712; --background-100:#111827;
  --background-soft-50:#111827; --background-soft-100:#111827;
  --background-soft-200:#1f2937; --background-soft-400:#1f2937;
  --title-50:rgb(255 255 255 / .8); --text-50:#9ca3af;
  --text-100:#9ca3af; --text-200:#6b7280;
  --base-50:#1f2937; --base-100:#111827; --base-200:#374151;
}
```

Alongside these, token sets for buttons, inputs, badges, menus, and alerts are
ported from the same source (primary `#3758f9` / hover `#2237ee`; error
`#dc2626`; success `#16a34a`; warning `#f59e0b`; dropdown surface `#fff` light /
`#1f2937` dark). Error and success states in the SQL and result components must
consume these rather than inventing per-component colors.

Type scale and radii follow the reference: 0.75 / 0.875 / 1 / 1.125 / 1.25 / 1.5
/ 1.875 rem; 8px control radius, 12px card radius, 16px message-pill radius.

### Typography

DM Sans, vendored as woff2 under `wwwroot/fonts` with its SIL OFL license file —
the host is offline loopback, so a CDN font is not an option. Latin subset,
weights 400 / 500 / 700, `font-display: swap`. `font-family: "DM Sans",
system-ui, sans-serif` so a missing file degrades rather than breaks. Monospace
surfaces (SQL blocks, codes, the editor) use `ui-monospace, "Cascadia Mono",
Consolas, monospace` with no vendored font.

### Theme switching

Three states — **system**, **light**, **dark** — chosen from a segmented control
in the user menu, matching the reference.

- Stored in `localStorage` under `sqlagent.theme`.
- Applied by an inline script in `<head>` in `App.razor`, before the Blazor
  circuit connects. This is not optional: a server round trip would paint the
  wrong theme first and flash.
- `system` sets no class on `<html>`; a `@media (prefers-color-scheme: dark)`
  block carries the same overrides as `.dark`, so the two paths cannot drift.
- Changing the theme also re-initializes Mermaid (Phase D) so a rendered diagram
  follows the app.

### Shell geometry

Matching the reference: 300px sidebar, 20px padding, `background-50` surface,
1px right border in `base-100`. The main area is an inset rounded card
(`background-100`, `base-100` border, 12px radius) sitting on a
`background-soft-100` page.

- Collapse button reduces the sidebar to an icon rail; state persists in
  `localStorage` under `sqlagent.sidebar`.
- Below 1024px the sidebar becomes an overlay drawer with a scrim, opened by a
  hamburger in the main card header, closed by scrim click or Escape.
- Sidebar body scrolls; header and user card are pinned.

### Icons

One `Icon.razor` holding inline SVG paths (lucide-style, `currentColor`,
24×24 viewBox), keyed by a name parameter. No icon-font or npm dependency.
Provider glyphs are a database cylinder tinted per provider, carrying provider
initials (`MS`, `PG`).

### User identity

`UserCard.razor` shows `Environment.UserName` and the machine name, with an
initials avatar, and opens a menu containing **Settings**, **Theme** (segmented),
and **About** (version, store path, port). There is no Sign out: the only session
concept is the `sqlagent_session` cookie the launch token is exchanged for, and
no user record exists to sign out of.

### Settings page

`/settings` is a read-mostly page, added in Phase A and extended by later phases.
It holds: theme (the same segmented control as the menu), the **LLM provider
status** panel — which in this build reports that no provider is configured and
points at `docs/runbook.md`, and which the composer's empty-state links to —
and an **Environment** panel showing version, store path, bind address and port,
and the active file-storage provider (Phase E). Nothing here is editable except
the theme; everything else is host configuration, which belongs in
`appsettings.json` and the runbook, not in a form that would need its own
validation and restart story.

### Phase A acceptance

- Every existing screen renders styled in both themes, with no unstyled
  fallback text and no layout shift on load.
- Theme survives reload with no flash; `system` follows the OS.
- Sidebar collapses, persists, and becomes a drawer under 1024px.
- `/settings` renders theme, provider status, and environment panels.
- Connections, Workspace, and the schema rail all still work — Phase A changes
  appearance only, no behavior and no routes.
- bUnit tests: sidebar renders its sections; user card menu opens and reports the
  OS account; collapse toggles the expected class.

## Phase B — Chat persistence and navigation

Priority **P0**. Depends on A. Split in two during design: **B1** ships
persistence, message-level database context, and history; **B2** ships projects
and search.

B1 has its own spec —
[2026-08-12 phase B1](2026-08-12-web-ui-phase-b1-chat-persistence-design.md) —
which supersedes this section wherever the two disagree. What follows is the
shape of the whole of B, with each part marked.

### Migrations come first (B1)

`Program.cs` calls `Database.EnsureCreatedAsync()`, and there are no migrations.
`EnsureCreated` never alters an existing database, so new tables would silently
never appear on any store that already exists — and Phase C adds columns to
`TablePolicy`.

Phase B1 therefore begins by introducing EF Core migrations:

1. An initial migration matching today's model exactly.
2. `MigrateAsync()` replaces `EnsureCreatedAsync()`.
3. A one-time baseline shim: a store whose tables exist but which has no
   `__EFMigrationsHistory` was created by `EnsureCreated`; startup stamps the
   initial migration as applied before migrating, otherwise `MigrateAsync` tries
   to re-create existing tables and throws.
4. Each later phase adds its own migration, B2's `Project` included.

The shim is covered by an integration test that boots against a store created the
old way. A migration that throws stops the host rather than degrading.

### Entities

```
Chat                 Id, Title, CreatedAt, UpdatedAt, LastMessageAt       (B1)
                     ProjectId?, index ProjectId                          (B2)
                     index LastMessageAt

ChatMessage          Id, ChatId, Sequence, Role, Text, CreatedAt,         (B1)
                     GeneratedSql?, OutcomeKind, ErrorCode?,
                     RowCount?, ElapsedMs?, Truncated
                     index (ChatId, Sequence) unique

ChatMessageDatabase  Id, ChatMessageId, DatabaseConnectionId?,            (B1)
                     DatabaseName
                     index ChatMessageId
                     index (ChatMessageId, DatabaseName) unique

Project              Id, Name, Description?, CreatedAt, UpdatedAt         (B2)
                     unique index Name
```

- `Role` is `User` | `Assistant`.
- `OutcomeKind` is `None` | `QueryResult` | `Clarification` | `Error` in B1;
  `ConfirmationRequired` and `SchemaDiagram` are added by Phase D, with the
  components that render them.
- **Databases attach to a message, not to a chat.** `ChatMessageDatabase` records
  the set the message was sent with. The composer's chips carry over to the next
  message until removed, so the context does not have to be re-attached, but each
  sent message keeps its own snapshot.
- `DatabaseConnectionId` is nullable and `DatabaseName` is not. Deleting a
  `DatabaseConnection` nulls the id across history and leaves the name, rather
  than deleting history or leaving a dangling id nothing can render.
- `ProjectId` null means an ungrouped chat that appears only in History.
- Deleting a project prompts: keep the chats (set `ProjectId` null) or delete
  them too. No silent cascade.
- Deleting a chat cascades to its messages and their attachment rows.
- `ModelId` is deliberately absent until the phase that selects models.

### Result rows are not persisted

`QueryAuditLog` already documents that result rows are never stored, and that
stance holds here. A reloaded chat shows message text, the SQL block, and the
row-count / duration / truncation metadata — not the previous grid. The SQL block
returns with Run available instead.

This is a visible behavior, stated plainly in `docs/web-ui.md`: rows seen before
a reload do not come back; re-run to see them. It also keeps the local store from
becoming a shadow copy of production data.

### Services

In `SqlAgent.Storage`, scoped, invoked through the existing `ScopedRunner`:

- `ChatService` (B1) — `ListHistoryAsync()`, `GetChatAsync()`,
  `CreateChatAsync`, `RenameChatAsync`, `DeleteChatAsync`, `AppendMessageAsync`
  (assigns `Sequence`, writes attachments, updates `LastMessageAt`).
- `ChatTurnService` (B1) — one turn end to end: persist the user message, branch
  on the number of attached databases, call `NlQueryService` when there is
  exactly one, persist the assistant message. Zero and several return the stable
  codes `no_database_attached` and `multiple_databases_unsupported`; the second
  disappears when the web-service phase brings a tool-calling loop.
- `ProjectService` (B2) — `ListProjectsAsync()` with chat counts,
  `MoveChatAsync`, create, rename, delete.
- `SearchAsync(term)` (B2) — chats by title and message text, projects by name,
  databases by name.

### Screens and interactions

**New chat** (`/`, B1): centered hero ("How can I help with your data?"), the
composer, and SQL-flavoured suggestion chips — *Explain this schema*, *Show table
relationships*, *Find the largest tables*, *Recent rows from…*. Chips **prefill
the composer and focus it** rather than sending immediately, so the question can
be edited first — and so that with no LLM configured a chip click does not
produce an instant `llm_not_configured` panel. The `Chat` row is created **lazily
on first send**, so opening New Chat and navigating away leaves no empty chat
behind. Title is the first user message truncated to 60 characters (there is no
LLM to summarize), editable afterwards.

**Attachment menu** (B1): the composer's attach button opens a menu whose
Databases section lists saved connections by name — the same name MCP addresses
them by. Choosing one adds a chip above the textarea. With no connections saved
the menu shows an empty state linking to `/connections`. Phase E adds a Files
section to this same menu.

B1 also drops `Workspace.razor`'s tab strip and moves it from `/` to `/sql`,
where it stays permanently, and renders assistant messages through the restyled
`ChatOutcome` until Phase D replaces it.

**History section** (B1): grouped by `LastMessageAt` in local time into Today,
Yesterday, Previous 7 days, Previous 30 days, Older. Active row highlighted. A
`⋮` menu on hover offers Rename and Delete in B1, plus Move to project in B2.
Delete asks for confirmation through `Modal` and its `Footer` slot, rendered from
`MainLayout` through `DialogService` so it is not trapped inside the mobile
drawer's transform.

**Projects section** (B2): label with an add button, folder rows with chat counts,
collapsible. Create and rename through a modal. Chats nest under an expanded
project.

**Search** (B2): a modal over the app, also on `Ctrl`/`Cmd`+`K`. Searches chat
titles, message text, project names, and database names; results grouped by kind;
arrow keys plus Enter to open. SQLite `LIKE` with an escaped pattern (`%`, `_`,
and the escape character itself), capped at 50 hits per kind — no FTS table,
because the corpus is one person's history.

### Phase B acceptance

B1:

- A chat survives reload with its messages, their order, and the database
  snapshot each message was sent with.
- New Chat abandoned without sending creates no row.
- Sending with zero or several databases attached produces the documented codes,
  visible again after a reload.
- History buckets correctly across day boundaries; rename and delete work from
  the `⋮` menu, delete behind a confirmation.
- Migration test: a store created by `EnsureCreated` migrates without error and
  keeps its data.
- `/` is the chat, `/chat/{id}` opens a stored one, `/sql` runs SQL.

B2:

- A chat lands in the right project, and moving it between projects works.
- Project deletion offers both outcomes and honors the choice.
- Search finds a chat by title, by message body, and a database by name.

## Phase C — Databases and access control

Priority **P1**. Depends on A and B1.

### Sidebar section

Between Search and Projects, styled identically to Projects: a "Databases" label
with an add button, then a collapsible list. Each row carries the provider glyph,
the name, and a connection-status dot reflecting the last test **in this session
only** — there is no background poller. Clicking a row or the add button
navigates to the config page.

### Config page `/database/{id?}`

Three stacked panels; the second and third appear only after a successful
connection test.

**1. Connection.** Name, DBMS select (SQL Server / Postgres), connection string,
read-only toggle, Test, Save, Delete. The existing secret rules are preserved
verbatim: the connection-string field starts blank when editing, and blank on
save keeps the stored secret. Test reports server version and elapsed time.
Provider exception text is never rendered — it can echo a connection string.

**2. Objects.** Tables and views grouped by schema, with a name filter and a
per-object access level as a three-way segmented control:

| Level | `TablePolicy` |
|---|---|
| Not visible | `IsVisible = false` |
| Read-only | `IsVisible = true`, `CanRead = true`, `CanWrite = false` |
| Full access | `IsVisible = true`, `CanRead = true`, `CanWrite = true` |

A schema header row sets every object beneath it at once. Views offer only Not
visible and Read-only. Changing any level invalidates `SchemaCache` for the
connection, as `SetVisibilityAsync` already does.

**3. Structure permissions.** A master toggle plus per-operation checkboxes:
create table, alter table, drop table, create index, drop index, truncate. All
off by default. The panel states that allowed operations still require
per-statement confirmation in chat.

### Backend: view extraction

`DatabaseSchema` gains `Views` as a **defaulted trailing parameter** so no
existing call site breaks:

```csharp
public record DatabaseSchema(
    IReadOnlyList<SchemaTable> Tables,
    IReadOnlyList<SchemaView>? Views = null)
{
    public IReadOnlyList<SchemaView> ViewList => Views ?? [];
}

public record SchemaView(string Schema, string Name, IReadOnlyList<SchemaColumn> Columns);
```

`SchemaModel.Build` gains an optional view-row parameter, and `SchemaModel.Filter`
applies the same visibility predicate to views. Both providers' catalog queries
gain a table-type discriminator (`INFORMATION_SCHEMA.TABLES.TABLE_TYPE` on SQL
Server; `pg_class.relkind` on Postgres) and select view columns from the same
column catalog they already query. Materialized views are out of scope.

### Backend: per-object write policy

`SqlPolicy` gains a per-object write check layered on the existing
connection-level read-only check. New codes:

- `policy_denied_readonly_object` — the statement writes to an object whose level
  is Read-only.
- `policy_denied_view_write` — the statement writes to a view.

`TablePolicy` gains an `ObjectKind` column (`Table` | `View`) for display and
filtering. The existing unique index on
`(DatabaseConnectionId, SchemaName, TableName)` still holds: neither SQL Server
nor Postgres allows a table and a view to share a name within a schema.

### Backend: DDL classification and permissions

`SqlStatementKind` gains `Ddl`. `ParsedStatement` gains
`DdlOperation` — `CreateTable`, `AlterTable`, `DropTable`, `CreateIndex`,
`DropIndex`, `Truncate`, `Unsupported`.

Permissions live on `DatabaseConnection` as one `[Flags] AllowedDdl` column
(`None` default). Anything mapping to `Unsupported` — `GRANT`, `EXEC`,
`CREATE VIEW`, procedure calls, everything currently caught by
`SqlStatementKind.Other` — remains denied unconditionally with the existing
behavior. Denials of mapped-but-unpermitted operations return
`policy_denied_ddl`, naming the operation.

### Confirm-before-run, enforced in the service

`QueryExecutionService.ExecuteSqlAsync` takes an explicit `confirmed` flag.

- A `Ddl` statement, or a `Write` statement generated by the model, executes only
  when the caller passes `confirmed: true`; otherwise it is denied with
  `ddl_confirmation_required`.
- `NlQueryService` never sets the flag. A model-generated DDL or DML statement
  returns as `NlResponseKind.ConfirmationRequired`, carrying the SQL and the
  operation name, and is **not executed**.
- The chat UI's confirm dialog is what calls back with `confirmed: true`.
- Hand-written SQL run from the scratchpad passes `confirmed: true` directly: the
  user typed it and pressed Run.
- The MCP server shares these services and has no confirmation surface, so it
  cannot execute DDL or model-generated writes at all — it receives
  `ddl_confirmation_required`. This is the intended default for a headless
  caller.

Extending confirmation to model-generated `INSERT`/`UPDATE`/`DELETE` (which
auto-execute today on a writable connection) is a deliberate widening: an
unattended `DELETE` the model got subtly wrong is as damaging as a `DROP`, and one
dialog covers both.

### Phase C acceptance

- Views appear in the object browser and in the schema handed to the model;
  hidden views are absent from both.
- Each access level produces the documented `TablePolicy` state, and a write to a
  Read-only object is denied with `policy_denied_readonly_object`.
- A write to a view is denied with `policy_denied_view_write`.
- With `AllowedDdl = None`, `DROP TABLE` is denied `policy_denied_ddl`.
- With drop-table allowed, the same statement without `confirmed` is denied
  `ddl_confirmation_required`, and with `confirmed` it executes.
- `EXEC` and `GRANT` remain denied regardless of every toggle.
- Changing an access level invalidates `SchemaCache`.
- Provider integration tests (opt-in, existing fixtures) cover view extraction on
  both providers.

## Phase D — Chat-embedded components

Priority **P1**. Depends on A, B1, C.

### Message rendering

User messages are right-aligned pills (`background-soft-100`, 16px radius,
max-width 70%) with copy and edit actions on hover. Assistant messages are
full-width with no bubble, preceded by an agent label and icon, followed by an
action row (copy, regenerate, `⋮`). Assistant text renders through **Markdig**
with raw HTML disabled, server-side — no client-side markdown dependency, and no
path for injected markup.

### SqlBlock

A header strip with a `SQL` label, the dialect badge (`T-SQL` / `PostgreSQL`), and
Copy / Edit / Run. The body is syntax-highlighted by `SqlHighlighter`, a small C#
tokenizer (keywords, strings, numbers, comments, identifiers → spans) rather than
a second CodeMirror instance per block. `docs/web-ui.md` already records that
CodeMirror is manual-test-only because bUnit runs no JS; a tokenizer is
unit-testable and renders correctly on the first server paint.

Statements classified `Ddl` or `Write` get an amber warning band naming the
operation and target ("This will drop table `orders`"), and Run goes through
`ConfirmDialog` before calling the service with `confirmed: true`. Read
statements run on a single click. Results render as a `DataTable` beneath the
block, for the live session only.

### DataTable

Sticky header with column names; a row-number gutter; `NULL` rendered as a dimmed
literal, distinguishable from the string `"NULL"`; long cell values truncated
with an expand affordance; client-side paging (25 / 50 / 100 with "showing 1–25
of 431"); CSV and JSON export through the existing `ResultExport` and
`download.js`; the row-cap truncation notice. Horizontal overflow scrolls inside
the block — the page never scrolls sideways.

The CSV formula-injection stance from the previous spec is unchanged and stays
documented: values are exported verbatim.

### ErDiagram

Mermaid `erDiagram` generated by `MermaidSource`, a pure function over the
**policy-filtered** schema, so a hidden table or view cannot appear in a picture.
Renders FK relationships and PK/FK column markers.

`mermaid.min.js` is vendored under `wwwroot/lib/mermaid` with its MIT license and
**loaded lazily via dynamic import on first diagram** — it is roughly 3MB and does
not belong in the startup path of a chat that never draws one. Themed by passing
CSS token values as Mermaid `themeVariables`, re-initialized on theme change.
Controls: zoom in / out / reset, fullscreen, download SVG.

Entry points are the composer's Tools menu and an attached database's chip menu.
The result is stored as an assistant message with `OutcomeKind = SchemaDiagram`;
a diagram needs exactly one attached database, and the picture
re-derives from the live schema on reload rather than persisting a stale snapshot.

### Composer

Auto-growing textarea capped at 40vh; Enter sends, Shift+Enter inserts a newline;
the attach button and its chips (B1 for databases, E for files); a **Tools** menu
carrying real actions — Schema diagram (renders locally, needs no LLM), Explain
schema (prefills the composer, like the suggestion chips), Open scratchpad; the
model selector with its empty state; and a circular send button that becomes a
stop button while a request is in flight, cancelling the existing
`CancellationTokenSource` and surfacing `execution_canceled` exactly as today.

The assistant action row's **regenerate** re-sends the preceding user message and
replaces the assistant message in place, rather than appending a second answer.

The model selector lists the models the SQL Agent web service offers, once that
service exists. Until then it shows "No model configured" and links to Settings,
and chat continues to return the `llm_not_configured` panel — the same
explanatory panel as today, not a raw code.

### ScratchPad

A collapsible panel on the chat page holding the existing `SqlEditor`
(CodeMirror, Ctrl+Enter), Run / Cancel, and a `DataTable`. Opened from a SQL
block's Edit action or the chat header. Runs with `confirmed: true`.

### Phase D acceptance

- A read SQL block runs on one click and renders results in a `DataTable`.
- A DDL or write SQL block cannot run without confirmation, verified at the
  service boundary and not only in the UI.
- `DataTable` pages, collapses long values, renders `NULL` distinctly, and
  exports CSV and JSON.
- `MermaidSource` omits hidden objects — unit-tested without a browser.
- The scratchpad round-trips SQL from a block, runs it, and exports.
- Mermaid rendering, CodeMirror behavior, and downloads are added to the manual
  checklist.

## Phase E — File attachments

Priority **P2**. Depends on A, B1. No dependents.

The attachment menu, its chips, and the per-message attachment model already
exist from B1, where databases are what gets attached. E adds a second kind of
thing to the same menu and the same chip row; nothing about the interaction is
new here, only the storage seam below it.

### The seam

`SqlAgent.Core/Files/`:

```csharp
public interface IFileStorageProvider
{
    string Key { get; }                                    // "local-disk"
    Task<StoredFile> SaveAsync(FileUpload upload, CancellationToken ct = default);
    Task<Stream?> OpenReadAsync(string storageKey, CancellationToken ct = default);
    Task<bool> DeleteAsync(string storageKey, CancellationToken ct = default);
}

public record StoredFile(string StorageKey, string Url, long SizeBytes);
public record FileUpload(string FileName, string ContentType, Stream Content);
```

`IFileStorageProviderRegistry` mirrors `IDatabaseProviderRegistry`, selecting by a
configured key (`SqlAgent:Files:Provider`, default `local-disk`).
`LocalDiskFileStorageProvider` lives in `SqlAgent.Storage` beside `SecretStore` —
both are local-persistence infrastructure. A remote provider would be its own
project, like the database providers. **ADR-0006** records this boundary and the
loopback-URL limitation.

### Local disk provider

Writes to `{storeDir}/files/{yyyy}/{MM}/{guid}{ext}`, with the directory locked
down the way `LaunchUrlFile` already does it (mode 600 on Unix; an explicit
Windows ACL granting only the service account and local administrators, with
inheritance off). The client's filename is **never** used on disk — a GUID is,
with the display name kept in the database. That removes path traversal as a
category rather than sanitizing for it. Returns
`http://127.0.0.1:{port}/files/{id}`.

### Serving files

`GET /files/{id}` sits behind the existing origin and token/session middleware.
Serving user-supplied files from the app's own origin is a stored-XSS vector — an
uploaded `.svg` or `.html` would otherwise execute with the app's cookie — so the
endpoint always sends:

- `Content-Disposition: attachment` with a quoted, encoded filename,
- `X-Content-Type-Options: nosniff`,
- `Content-Security-Policy: sandbox`,
- and a neutral `application/octet-stream` for HTML-ish content types rather than
  the stored one.

Nothing is ever rendered inline.

### Entity and lifecycle

```
MessageAttachment  Id, ChatMessageId, FileName, ContentType, SizeBytes,
                   ProviderKey, StorageKey, Url, CreatedAt
                   index ChatMessageId
```

It sits beside B1's `ChatMessageDatabase` — same owner, same lifetime, same chip
row in the composer — and differs only in having bytes behind it.

Upload happens when the file is picked, so progress and errors surface before
sending; the resulting `StoredFile` is held in circuit state and the rows are
written when the message is sent. A file uploaded and never sent is therefore a
blob with no row. Those orphans are cleaned up two ways: the circuit deletes its
own un-bound `StorageKey`s on teardown, and startup sweeps the storage directory
for blobs older than 24 hours with no matching `MessageAttachment` row, which
covers anything a crash left behind. The 24-hour floor exists so the sweep can
never race an upload in flight in another circuit.

Deleting a message, chat, or project deletes its blobs — best-effort, logged on
failure, so disk usage tracks what is visible in the UI.

Limits are enforced server-side, not only in the picker:
`SqlAgent:Files:MaxBytes` (default 25 MB) per file, 10 attachments per message.
Rejections return `file_too_large` and `file_rejected`.

### Handoff to the model

`LlmSqlRequest` gains `Attachments` as `(FileName, ContentType, Url)`. Nothing
consumes it yet — `UnavailableLlmSqlGateway` still fails closed — so the seam is
complete and inert. The docs state plainly that a loopback URL is fetchable by a
locally-hosted model but not by a cloud provider, and that a remote storage
provider is the answer when one is wired.

### Phase E acceptance

- A file uploads, appears as a chip in the composer, and persists with the sent
  message.
- Over-cap files and over-count messages are rejected server-side with the
  documented codes.
- `GET /files/{id}` requires the session, sends the hardening headers, and never
  serves HTML inline.
- Deleting a chat removes its blobs; abandoned uploads are swept.
- An uploaded file's name never determines a path on disk.

## Error handling

Unchanged discipline throughout: stable codes from Core rendered through
`OutcomeMessage`; provider exception text never shown to the user (it can echo a
connection string); details to the server log; `WorkArea`'s `ErrorBoundary`
retained as the final backstop, including its `LocationChanged` recovery.

New codes introduced by this work:

| Code | Meaning |
|---|---|
| `policy_denied_readonly_object` | Write to an object whose access level is Read-only |
| `policy_denied_view_write` | Write targeting a view |
| `policy_denied_ddl` | DDL operation not permitted by `AllowedDdl` |
| `ddl_confirmation_required` | Permitted DDL or model-generated write, not confirmed |
| `file_too_large` | Upload exceeds `SqlAgent:Files:MaxBytes` |
| `file_rejected` | Upload refused (count cap, unreadable stream, storage failure) |

## Testing

**bUnit component tests** for every new component: sidebar sections (database,
project, history grouping), user card menu, config page panels and access-level
control, `SqlBlock`'s confirm gate, `DataTable` paging / collapse / `NULL`
rendering, composer send and stop, attachment chips.

**Unit tests** for `SqlHighlighter`, `MermaidSource` (including that hidden
objects are omitted), the access-level ↔ `TablePolicy` mapping, DDL
classification and the `AllowedDdl` flag check, day-bucketing of history, and
search pattern escaping.

**Integration tests** for the migration baseline shim (a store created by
`EnsureCreated` migrates cleanly), the `/files/{id}` endpoint's auth and headers,
and the `confirmed`-flag enforcement at the service boundary.

**Provider integration tests** (opt-in, existing Docker fixtures) for view
extraction on SQL Server and Postgres.

**Manual checklist** in `docs/web-ui.md` grows to cover what bUnit cannot reach:
Mermaid rendering and theming, CodeMirror highlighting and Ctrl+Enter, file
download, the file picker, theme application with no flash on load, sidebar
collapse and the mobile drawer.

## Documentation

- `docs/web-ui.md` rewritten for the new shell and screens, including the
  rows-not-persisted behavior, how databases attach to a message and travel with
  it, the confirm-before-run model, and the expanded manual checklist.
- `docs/runbook.md` gains file-storage configuration and the DDL-permission
  model.
- `docs/adr/0006-file-storage-provider-boundary.md` added.
- `README.md` screen list updated.

## Phasing and priority

| Phase | Priority | Delivers | Depends on |
|---|---|---|---|
| A | P0 | Design system, shell, sidebar, themes, user card | — |
| B1 | P0 | Migrations, persisted chats, message-level database context, history, `/sql` | A |
| B2 | P0 | Projects, search, `Ctrl`/`Cmd`+K | B1 |
| C | P1 | Databases section, config page, views, access levels, DDL permissions | A, B1 |
| D | P1 | SQL blocks, data tables, ER diagrams, scratchpad | A, B1, C |
| E | P2 | File storage seam, attachments | A, B1 |

Each phase ends green — build, tests, working app — and is independently
shippable. Stopping after B1 yields a styled chat application whose conversations
survive a restart; B2 adds grouping and search; stopping after C adds the access
control; D and E are additive.

### Work outside these phases

Two things this plan depends on, or bumps into, that are not Web UI work:

| Work | When | Why it is listed here |
|---|---|---|
| SQL Agent web service hosting several models, and the gateway client that calls it | After the Web UI | It is what makes the chat answer anything. Its design decides whether the gateway stays one-shot SQL generation or becomes a tool-calling loop over the MCP tool set — see [Non-goals](#non-goals). |
| MCP addressing databases by connection name | Any time; small | `McpToolService` currently requires a GUID and answers `invalid_database_id` to a name, while the product statement is that both MCP and the chat name databases by their connection name. Names carry a unique index, so the lookup is safe. Name becomes primary, GUID keeps working so configured clients do not break. Touches `McpToolService`, `DatabaseTools`, `docs/ide-plugin-setup.md`, and two test classes. |

## Risks

| Risk | Mitigation |
|---|---|
| Migration baseline shim corrupts an existing store | Integration test against a store created by `EnsureCreated`; the shim only inserts a history row, never alters user tables. |
| DDL enforcement widens the agent's blast radius | Off by default, per-operation, per-connection; confirmation enforced in the service, not the UI; `Unsupported` statements stay denied; MCP cannot execute DDL at all. |
| Mermaid's 3MB asset slows startup | Lazy dynamic import on first diagram; never loaded otherwise. |
| Serving uploads from the app origin enables stored XSS | Attachment disposition, `nosniff`, sandbox CSP, neutral content type for HTML-ish files, nothing inline. |
| Hand-written CSS drifts from the reference | One token block, authored once from the extracted values; components consume tokens only, never literal colors. |
| Phase D depends on C, so a mid-C stop leaves D unstarted | Phase order is dependency-driven precisely so each stopping point is coherent. |
