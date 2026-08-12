# Web UI Phase B1: chat persistence, message-level database context, history

- **Status:** approved, ready for implementation planning
- **Date:** 2026-08-12
- **Depends on:** Phase A (shipped)
- **Parent spec:** [2026-08-12 web UI overhaul](2026-08-12-web-ui-overhaul-design.md)

## Context

Phase A gave the UI a design system and a shell. The chat behind it is still the
one `Workspace.razor` has always had: a `List<TranscriptEntry>` field that a page
reload erases. There are no chats, no history, no migrations — `Program.cs` calls
`EnsureCreatedAsync`, which never alters a store that already exists.

Two things changed in the product picture after the parent spec was written, and
they change what Phase B has to build:

**SQL Agent is a local service that mediates database access for language
models.** It stores connection strings in a secret store, describes schemas,
enforces per-object visibility and read/write policy, and executes validated SQL.
MCP is one way to reach that mediation — the canonical one for IDE hosts
(ADR-0001). The built-in chat is the **alternative** way to reach the same
mediation, for people who are not in an editor. Both address databases by the
**name** given in connection settings, and both go through the same
`QueryExecutionService` and the same policy. Neither is a place where a person
pastes a connection string or a page of production rows.

**A chat is not bound to one database.** Databases are *context attached to a
message*, the way files will be: zero, one, or several, added from the composer's
attachment menu. The parent spec's `Chat.DatabaseConnectionId` column and its
"database pill in the chat header" are wrong and are corrected there.

The model itself is still not wired, and stays unwired through B1 and B2. Calls
will go to a SQL Agent web service hosting several models, developed after the
Web UI work. Until it exists `UnavailableLlmSqlGateway` keeps failing closed.

## Goals

1. Introduce EF Core migrations, safely, on stores that already exist.
2. Persist chats and messages, including the set of databases each message was
   sent with.
3. Give the chat a real home: `/` for a new chat, `/chat/{id}` for an existing
   one, day-bucketed history in the sidebar with rename and delete.
4. Add the composer's attachment menu with databases in it.
5. Keep the manual SQL editor reachable, at `/sql`.

## Non-goals

| Out of scope | Why |
|---|---|
| Projects, search, `Ctrl`/`Cmd`+K | Phase B2. B1's migration work makes adding `Project` and `Chat.ProjectId` later a single migration file. |
| File attachments | Phase E. B1 builds the attachment menu; E adds a second kind of thing to it. |
| Any real model call | Its own phase, after B1 and B2, against the SQL Agent web service. |
| Multi-database questions | The gateway takes one schema and returns one SQL string. Two or more attached databases return a stable code until the service phase brings a tool-calling loop. |
| `ModelId` on a chat | A column nothing writes. It arrives with the phase that selects models. |
| Streaming responses | Nothing to stream from yet. |
| Rendering result rows from history | Rows are never persisted. See [Result rows](#result-rows-are-not-persisted). |

## Decisions taken before design

| Decision | Alternative rejected | Why |
|---|---|---|
| Databases attach to a **message**, and the composer's chips carry over to the next message until removed | Attaching to the chat; or requiring re-attachment every message | The transcript stays honest about what each question was asked against, without making the user re-attach on every turn. |
| Each attachment stores the connection **name** as a snapshot beside a nullable connection id | Storing the id alone | Connections get renamed and deleted. A transcript that forgets which database a question was asked against is worse than a dangling id. |
| The user message is persisted **before** the model is called | Persisting the whole turn after the answer arrives | A failed call, a dead circuit, or a closed tab must not cost the typed question. |
| Failed answers are persisted too, `llm_not_configured` included | Storing only successful answers | Otherwise a reload silently drops half the turns and the transcript stops matching what the user saw. |
| `Project` does **not** ship in B1 | Shipping all three entities in one migration | Migrations are cheap once B1 has built them. The project's own rule — nothing in the codebase that nothing renders — wins. |
| One turn is orchestrated by `ChatTurnService`, separate from `ChatService` | Orchestrating in `Chat.razor` | The whole turn becomes unit-testable without bUnit, and the page stays thin. |
| Delete confirmation uses `Modal` with its `Footer` slot | Building `ConfirmDialog` early | `Modal.Footer` was made optional in Phase A for exactly this caller. `ConfirmDialog` belongs with Phase D's run-confirmation gate. |
| A failed migration stops the host | Starting anyway and degrading | A host whose store is half-migrated will corrupt or lose data. Refusing to start is the safe failure. |
| The SQL editor keeps a permanent page at `/sql` | Retiring the page in D once `ScratchPad` exists | A full-screen editor is useful on its own for long queries and wide results. `ScratchPad` (D) and `/sql` render the same components. |

## Architecture

All work is in `SqlAgent.Host` and `SqlAgent.Storage`. Core and the providers are
untouched.

### Migrations come first

`EnsureCreatedAsync` never alters an existing database, so new tables would
silently never appear on any store that already exists. B1 therefore begins with
migrations, in this order:

1. **`InitialCreate`** describes today's six entities *exactly* —
   `DatabaseConnection`, `TablePolicy`, `SchemaCache`, `QueryAuditLog`,
   `AppSetting`, `Secret`. Nothing new. If this migration described the new
   tables too, step 3 would stamp it as applied and those tables would never be
   created on an existing store.
2. `MigrateAsync()` replaces `EnsureCreatedAsync()` in `Program.cs`.
3. **Baseline shim.** A store whose tables exist but which has no
   `__EFMigrationsHistory` was created by `EnsureCreated`. Startup detects that
   (a known table present, the history table absent), inserts the `InitialCreate`
   row into `__EFMigrationsHistory`, and only then migrates. Without it
   `MigrateAsync` tries to re-create existing tables and throws. The shim writes
   one history row and never touches a user table.
4. **`ChatPersistence`** adds B1's tables.

The shim lives in its own class in `SqlAgent.Storage` — not inline in
`Program.cs` — so an integration test can boot it against a store created the old
way. Later phases add their own migrations and never revisit this.

If migration throws, the exception is logged with the store path and the host
stops. See the decision table.

### Entities

```
Chat                  Id, Title, CreatedAt, UpdatedAt, LastMessageAt
                      index LastMessageAt

ChatMessage           Id, ChatId, Sequence, Role, Text, CreatedAt,
                      GeneratedSql?, OutcomeKind, ErrorCode?,
                      RowCount?, ElapsedMs?, Truncated
                      unique index (ChatId, Sequence)

ChatMessageDatabase   Id, ChatMessageId, DatabaseConnectionId?, DatabaseName
                      index ChatMessageId
                      unique index (ChatMessageId, DatabaseName)
```

- `Role` is `User` | `Assistant`.
- `OutcomeKind` is `None` | `QueryResult` | `Clarification` | `Error` — only the
  values B1 writes. `ConfirmationRequired` and `SchemaDiagram` arrive in Phase D
  with the components that render them.
- Enums are stored as strings, like `QueryAuditLog.Decision`, so reordering a
  member cannot silently re-interpret existing rows.
- `DatabaseConnectionId` is nullable and `DatabaseName` is not. Deleting a
  connection nulls the id across history and leaves the name; the transcript
  still says what the question was asked against. This is the parent spec's
  stance on connection deletion, moved from the chat to the message.
- Deleting a `Chat` cascades to its messages and their attachment rows.
- All timestamps are UTC.

### Result rows are not persisted

`QueryAuditLog` already records that result rows are never stored, and that holds
here. A reloaded chat shows the message text, the generated SQL, and the
row-count / duration / truncation metadata — not the previous grid.

This is visible behavior, so the UI says it rather than rendering an empty table
that would imply the query returned nothing: a restored answer shows its
metadata line, its SQL, and an "open in editor" action to run it again.
`docs/web-ui.md` states it in prose as well.

### Services

Both live in `SqlAgent.Storage`, are scoped, and are invoked through the existing
`ScopedRunner` so no `DbContext` outlives a single user action.

**`ChatService`** — the store, nothing else:

- `ListHistoryAsync(int take)` → chats by `LastMessageAt` descending, projected
  without messages.
- `GetChatAsync(Guid id)` → the chat, its messages by `Sequence`, and each
  message's attachments.
- `CreateChatAsync(string title)`.
- `AppendMessageAsync(...)` → assigns `Sequence`, writes attachment rows, moves
  `LastMessageAt` and `UpdatedAt`.
- `RenameChatAsync(Guid id, string title)`, `DeleteChatAsync(Guid id)`.

`Sequence` is `max + 1` within the action's scope. The unique index on
`(ChatId, Sequence)` is the backstop for two browser tabs appending to one chat:
the violation is caught and the append retried once.

**`ChatTurnService`** — one turn, start to finish:

1. Persist the user message with its attachment snapshot.
2. Branch on the number of attached databases:
   - **0** → an assistant message, `OutcomeKind = Error`, code
     `no_database_attached`. No call is made.
   - **1** → `NlQueryService.AskAsync(connectionId, question, ct)`, which applies
     policy, executes, and audits exactly as every other surface does.
   - **2 or more** → an assistant message, `OutcomeKind = Error`, code
     `multiple_databases_unsupported`. Picking the first attachment silently
     would be a lie about what was queried.
3. Persist the assistant message — outcome kind, generated SQL, error code, row
   count, elapsed milliseconds, truncation flag.

Because no gateway is configured, the single-database branch currently resolves
to `llm_not_configured`, which is stored like any other answer. That is the point:
persistence is exercised end to end with no key and no network.

### State

`AppState` gains, alongside the existing connection selection and its two
documented events:

- `ActiveChatId` and a `ChatsChanged` event, so the sidebar's history section and
  the chat page stay in step. This is the same reason `AppState` exists at all:
  Blazor siblings do not re-render each other.
- `PendingSql`, set when an answer's "open in editor" navigates to `/sql`, read
  and cleared by that page.

Connection selection, `SchemaRail`, and the rail's picker are untouched; the rail
remains the visibility surface until Phase C.

## Screens

### Sidebar

Navigation becomes **New chat**, **SQL**, **Connections**, **Settings**. Search
joins it in B2. Below it: the history section, then — temporarily — the schema
rail, both scrolling in the sidebar body. The user card stays at the foot.

Before any of that, B1 removes the duplicated collapsed-sidebar styling carried
forward from Phase A (item 7): the collapsed state is described both in
`app.css`, keyed on the pre-paint `html.sidebar-collapsed` class, and in
`Sidebar.razor.css`, keyed on the class the circuit adds. They agree by hand,
nothing enforces it, and the split already caused two Phase A defects. Adding a
section to the sidebar is when it would cause a third.

### History section

Chats grouped by `LastMessageAt` in local time: Today, Yesterday, Previous 7
days, Previous 30 days, Older. Timestamps are stored in UTC and bucketed in local
time — the host and the browser are the same machine, so local time is the
user's time. The active chat's row is highlighted.

A `⋮` menu on hover offers **Rename** and **Delete**. Move to project arrives in
B2 with projects. Delete asks for confirmation through `Modal` and its `Footer`
slot.

When the sidebar is collapsed to its icon rail, the history section is hidden
rather than squeezed; the nav icons and the user card remain.

### Chat page

`/` is a new chat; `/chat/{id}` is an existing one.

- **Empty state:** a centered heading, the composer, and suggestion chips. Chips
  **prefill the composer and focus it** rather than sending — the question can be
  edited first, and one click cannot produce an instant error panel while no
  model is configured.
- **Lazy creation:** the `Chat` row is written on first send, so opening a new
  chat and navigating away leaves nothing behind. The title is the first user
  message truncated to 60 characters, editable afterwards.
- After the first send the URL becomes `/chat/{id}` via a replacing navigation,
  without a reload.
- **Messages:** user messages are right-aligned pills; assistant messages are
  full-width and render through the restyled `ChatOutcome` until Phase D replaces
  it with `SqlBlock` and `DataTable`.

Components live under `Components/Shared/Chat/`: `MessageList`, `UserMessage`,
`AssistantMessage`, `Composer`, `AttachmentMenu`, `AttachmentChips`.

### Composer and attachments

An auto-growing textarea. Enter sends, Shift+Enter inserts a newline. The send
button becomes a stop button while a request is in flight and cancels the same
`CancellationTokenSource` the SQL page already cancels.

The attach button opens a `Menu` with a **Databases** section listing saved
connections by name, with their provider badge. Choosing one adds a chip above
the textarea. Chips live in circuit state, travel as a snapshot with every sent
message, and carry over to the next message until removed with their `×`.
Removing a chip affects future messages only; sent messages keep their snapshot.

With no connections saved the menu renders `EmptyState` with a link to
`/connections`. A `Spinner` covers the in-flight request. Both components shipped
in Phase A with no caller; these are their callers.

### `/sql`

`Workspace.razor` loses its tab strip and becomes the SQL editor alone —
`SqlEditor`, Run, Cancel, `ResultGrid` — at `/sql`, and stays in the navigation
permanently. Phase D adds `ScratchPad`, a collapsible panel on the chat page
built from the same components; the page does not go away when it arrives.

## Error handling

Unchanged discipline: stable codes from the service layer, rendered through
`OutcomeMessage`; provider exception text is never shown to the user, because it
can echo a connection string.

Two codes are new, both raised by `ChatTurnService`:

| Code | Meaning |
|---|---|
| `no_database_attached` | The message was sent with no database in its context. |
| `multiple_databases_unsupported` | More than one database was attached; the current gateway takes one schema. |

## Testing

Phase A's lesson stands: bUnit renders markup but runs no browser, no CSS engine,
no focus model, and no circuit. Everything that depends on those is asserted
against DOM structure or stylesheet source text, or moved to the manual list.

**Integration**

- A store created by `EnsureCreated`, with rows in it, migrates without error and
  keeps its data. The highest-risk item in the phase.
- A fresh store migrates from empty.

**Unit**

- `ChatService`: `Sequence` assignment; day bucketing across midnight and across
  the 7- and 30-day edges; rename; delete cascading to messages and attachments.
- `ChatTurnService`: zero, one, and several attached databases; the user message
  is persisted even when the gateway throws; `llm_not_configured` is stored as an
  assistant message; the attachment snapshot records the name.

**bUnit**

- Chips carry over between sends and are removed by their `×`.
- The attachment menu's empty state renders when no connections exist.
- A restored assistant message renders its metadata and SQL, not an empty grid.
- History rows render in the right buckets from a fixed clock.

**Manual checklist additions** (`docs/web-ui.md`)

- Reload an open chat: messages, order, and attachment chips survive.
- Open a new chat, type nothing, navigate away: no chat appears in history.
- Send with no database attached, and with two attached: the explanatory panels.
- Delete a connection that a stored message referenced: the name still shows.
- Start the host against a store created before this phase: it migrates.
- Open the delete confirmation from the history `⋮` inside the mobile drawer:
  the dialog centres on the viewport.
- Tab through a closed drawer below 1024px: focus does not enter it.

## Carried forward from Phase A, closed here

| # | Item | How |
|---|---|---|
| 7 | Collapsed-sidebar styling duplicated between `app.css` and `Sidebar.razor.css` | Delete the duplicate set and keep one source of truth, the way the theme already works; scope the remaining selectors under the sidebar. |
| 1 | A `Modal` inside the mobile drawer resolves against the drawer's transform and rides off-screen | B1's delete confirmation is opened from the drawer, so this stops being theoretical. A circuit-scoped `DialogService` plus a host component in `MainLayout` renders dialogs outside the sidebar subtree — simpler than a portal and testable. |
| 3 | A closed drawer keeps its contents in the tab order, and focus is not restored to the hamburger | History rows multiply the off-screen tab stops. |
| 9 | `Spinner` and `EmptyState` shipped with no caller | Both get one in the composer. |
| 2 | `Modal`'s `autofocus` is unverified in a browser | One row on the manual checklist. If it does not fire, `UiInteractionTests.The_modal_close_button_autofocuses_…` is rewritten with it. |

Still open, and why they wait: item 8 (Safari does not focus a button on mouse
click) waits for B2's `Ctrl`/`Cmd`+K, which makes a document-level key listener
worth its cost; items 11 and 12 (two tests that do not test what their names
claim) go to B2; item 13 is a documented trap, not a defect.

## Definition of done

- `dotnet build SqlAgent.slnx --configuration Release` is clean and
  `dotnet test SqlAgent.slnx --configuration Release` is green, pre-existing
  tests included.
- A store created by the previous release migrates and keeps its data.
- A chat survives a reload with its messages, their order, and their attachment
  snapshots.
- A new chat abandoned without sending leaves no row.
- History buckets correctly across day boundaries; rename and delete work from
  the `⋮` menu, delete behind a confirmation.
- Sending with zero or several databases attached produces the documented codes,
  and both are visible in the reloaded transcript.
- `/` is the chat, `/chat/{id}` opens a stored one, `/sql` runs SQL, and
  `/connections` and `/settings` are unchanged.
- No component stylesheet contains a literal color.
- `docs/web-ui.md` documents chat persistence, the attachment model, and the new
  manual checks.

## What B1 leaves to B2

Projects (`Project`, `Chat.ProjectId`, the sidebar section, move-to-project in
the `⋮` menu), search and its modal, `Ctrl`/`Cmd`+K, and Phase A carry-forward
items 8, 11, and 12.
