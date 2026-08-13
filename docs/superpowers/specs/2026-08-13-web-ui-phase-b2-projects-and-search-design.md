# Web UI Phase B2: projects, search, and a keyboard shortcut

- **Status:** approved, ready for implementation planning
- **Date:** 2026-08-13
- **Depends on:** Phase A and Phase B1 (both shipped)
- **Parent spec:** [2026-08-12 web UI overhaul](2026-08-12-web-ui-overhaul-design.md)
- **Predecessor:** [2026-08-12 phase B1](2026-08-12-web-ui-phase-b1-chat-persistence-design.md)

## Context

Phase B1 gave the UI persisted chats: migrations with a baseline shim, a chat
store, databases attached per message, a chat page at `/`, the SQL editor at
`/sql`, and a day-bucketed history sidebar with rename and delete. What it
deliberately left out is grouping and finding: every conversation lives in one
flat list ordered by recency, and the only way to reach an old one is to scroll.

B2 closes that. It adds projects, a search modal reachable from the keyboard,
and four debts earlier phases recorded rather than fixed.

The model itself is still not wired and stays unwired: calls will go to a SQL
Agent web service hosting several models, built after this Web UI work. Nothing
in this phase depends on that.

## Goals

1. Group chats into projects, created and renamed from the sidebar.
2. Search chats by title and by message text, and projects and databases by name,
   from a modal reachable with `Ctrl`/`Cmd`+`K`.
3. Close the four carried debts listed under [Debts](#debts-this-phase-closes).

## Non-goals

| Out of scope | Why |
|---|---|
| A `Description` on `Project` | The parent spec lists the column, but no screen renders it. This project's standing rule is that nothing lives in the codebase that nothing renders; it arrives with the place that shows it. |
| Scrolling to the matching message | A search hit opens its chat at the top and shows the matched snippet in the result row. Anchoring and scrolling needs interop and a story for restored messages, for modest gain on a personal corpus. |
| Relevance ranking, FTS | Without an FTS table, ranking degrades to guesswork. Hits are ordered by recency within their kind, which is honest about what the query knows. |
| Nested projects | Folders inside folders on a one-person history is structure without a user. |
| Persisting which projects are expanded | A third `localStorage` key beside theme and sidebar, for a state the user restores with one click. |
| Anything from phases C–E | Unchanged. |
| Copying the store before a migration | Recommended by B1's final review and still an open decision for the maintainer; it is not this phase's work. |

## Decisions taken before design

| Decision | Alternative rejected | Why |
|---|---|---|
| A chat in a project leaves the history list | Showing it in both places | Each chat appears in exactly one place, so "move to project" reads literally and the sidebar cannot show one conversation twice. |
| `Chat.ProjectId` carries a real foreign key with `Restrict` | A soft reference, as `ChatMessageDatabase` uses | That soft reference exists because a message's attachment snapshot must survive the connection being deleted. A project and its chats live in the same store, and deleting a project must be an explicit decision about its chats — `Restrict` makes the service the only way through. |
| Deleting a project asks: keep the chats or delete them | A silent cascade either way | It is the only operation in the phase that can destroy a conversation. |
| `ProjectService` writes return an outcome, not a `bool` | `bool` | "Name already taken" and "the project is gone" need different words in the UI, and a `bool` cannot tell them apart. |
| Search collapses message hits to one per chat | One row per matching message | A conversation that says the word fifteen times would otherwise push every other result off the list. |
| The search query runs on every keystroke, with no debounce | A debounce | The store is local and the corpus is one person's history; an artificial delay would only add drag. If it ever needs a brake, it belongs in the shortcut script that this phase adds anyway. |
| `ChatRow` and `NameDialog` are extracted from B1's `HistorySection` | Copying the markup into the project section | Two places that must be kept in step by hand is the exact defect shape this project has already paid for twice. |
| Move-to-project opens a dialog, not a submenu | Teaching `Menu` about submenus | Phase A's `Menu` has no submenu concept, and building one for a single caller is disproportionate. |

## Architecture

All work is in `SqlAgent.Host` and `SqlAgent.Storage`. Core and the providers are
untouched.

### Entities

One migration, `Projects`:

```
Project   Id, Name, CreatedAt, UpdatedAt          unique index Name
Chat      + ProjectId?                            index ProjectId
                                                  FK → Project, DeleteBehavior.Restrict
```

- `ProjectId` null means an ungrouped chat, which is what the history section
  lists. A chat with a project appears under that project and nowhere else.
- `Restrict` means the database refuses to delete a project that still has chats.
  That is deliberate: `ProjectService.DeleteProjectAsync` decides the chats' fate
  first, and no other path can delete a project by accident.

### Services

Both in `SqlAgent.Storage`, scoped, invoked through the existing `ScopedRunner`.

**`ProjectService`**

- `ListProjectsAsync()` → `ProjectSummary(Guid Id, string Name, int ChatCount)`,
  the count produced by one grouped query rather than a query per project.
- `ListChatsInProjectAsync(Guid projectId)` → the same `ChatSummary` the history
  list uses, so one row component renders both.
- `CreateProjectAsync(string name)` → `ProjectWriteResult`.
- `RenameProjectAsync(Guid id, string name)` → `ProjectWriteResult`.
- `DeleteProjectAsync(Guid id, ProjectDeleteMode mode)` → `bool`, where
  `ProjectDeleteMode` is `KeepChats` (sets `ProjectId` null, returning them to
  history) or `DeleteChats`.
- `MoveChatAsync(Guid chatId, Guid? projectId)` → `bool`. A null project moves
  the chat back to history, so "remove from project" and "move to another" are
  the same call.

```csharp
public enum ProjectWriteOutcome { Ok, NameTaken, NotFound }
public record ProjectWriteResult(ProjectWriteOutcome Outcome, Guid? Id = null);
```

A taken name is caught twice: by a read before the insert, so the dialog can say
so plainly, and by catching the unique-index violation as the backstop. That is
the same two-layer shape `ChatService` already uses for message sequence numbers,
and the backstop is what makes the read-then-write race harmless.

**`SearchService`**

```csharp
public enum SearchHitKind { Chat, Message, Project, Database }
public record SearchHit(SearchHitKind Kind, Guid TargetId, string Label, string? Snippet);

SearchAsync(string term) → IReadOnlyList<SearchHit>
```

- A blank or whitespace term never reaches the database and returns nothing.
- `%`, `_` and the escape character itself are escaped, and the query uses the
  three-argument `EF.Functions.Like` with an explicit escape character. Without
  it, searching for `50%` matches everything and `_` matches any character —
  silently, and inexplicably from the user's side.
- At most 50 hits per kind.
- Message hits collapse to one per chat, taken at the first match, with a snippet
  cut around it in memory after the rows come back. SQLite cannot do that cut,
  and the corpus does not need it to.
- Within a kind, hits are ordered by `LastMessageAt` descending — newest first.

**Changed in B1's code:** `ChatService.ListHistoryAsync` now excludes chats that
belong to a project. This is the phase's only change to existing *behaviour*, and
its existing tests change with it.

Two further changes to existing tests follow components rather than behaviour, and
each task names the tests it touches: `ChatRenameDialog`'s tests move to
`NameDialog` when the dialog is generalized, and `ChatPageTests` loses its `using`
alias when the page is renamed to `ChatPage`.

### Components

```
Components/Layout/
  ProjectSection.razor      collapsible project list + add                (new)
  HistorySection.razor      unchanged except it renders ChatRow           (B1)
  ChatRow.razor             one chat row: active state, ⋮ menu            (extracted)
  KeyboardShortcuts.razor   owns the document listener, renders nothing   (new)
Components/Shared/Chat/
  NameDialog.razor          ask for a name (chat rename, project create/rename)
  ChatRenameDialog.razor    DELETED — NameDialog supersedes it
  MoveToProjectDialog.razor pick a project, or none                        (new)
  SearchDialog.razor        the search modal                               (new)
Web/
  ShortcutService.cs        circuit-scoped: EscapePressed, SearchRequested (new)
wwwroot/js/
  shortcuts.js              one document keydown listener                  (new)
```

New glyphs: `folder`, `chevron-right`, `search`. The icon-inventory test grows
with them, as every phase's does.

### The sidebar

Top to bottom: navigation (gaining a Search row), the project section, the
history section, the schema rail, the user card.

A project row carries a folder glyph, its name, its chat count, and a chevron.
Expanding it lists its chats through the same `ChatRow` the history section uses.
Expansion state lives in the component and does not survive a reload.

The `⋮` menu gains **Move to project**, which opens a dialog listing the projects
and a "No project" row. Like rename and delete, it goes through `DialogService`
and therefore renders from `MainLayout` — below 1024px the sidebar's transform
would otherwise capture the dialog and carry it off-screen.

Two empty states, deliberately different: with no projects, only the heading and
its add button render, because "no projects" tells the user nothing they cannot
see; with no chats at all, the history section already says so and repeating it
would be noise.

### Search and the keyboard

The modal opens from the nav row and from `Ctrl`/`Cmd`+`K`, through
`DialogService`.

Inside the modal, the keyboard needs no JavaScript: focus is in the input, and
arrow keys, Enter and Escape are handled by its `@onkeydown`. Arrow keys move a
highlighted index, Enter opens the highlighted hit, Escape closes.

`Ctrl`/`Cmd`+`K` does need JavaScript — the shortcut must work wherever focus is,
and Blazor only hears elements it rendered. `wwwroot/js/shortcuts.js` installs one
document-level `keydown` listener, calls `preventDefault` so the browser's own
`Ctrl`+`K` does not take over, and invokes a `[JSInvokable]` on
`KeyboardShortcuts.razor`, which raises `ShortcutService.SearchRequested`.

That same listener closes Phase A's carried Safari defect. Safari does not focus
a `<button>` on a plain mouse click — a macOS convention — so Escape never bubbles
to a `Menu` opened by mouse, and the user menu cannot be dismissed with the
keyboard there. `Menu` and `Modal` therefore subscribe to
`ShortcutService.EscapePressed` **while open** and unsubscribe when closed,
keeping the symmetry Phase A established when it made the drawer's key handler
conditional to avoid a server round trip per keystroke.

## Error handling

Unchanged discipline: stable outcomes from the service layer, no provider or
exception text rendered anywhere.

`ProjectWriteOutcome` is not an error code shown to the user but a distinction the
UI acts on: `NameTaken` is reported beside the field in the dialog; `NotFound`
closes the dialog and refreshes the list, because the project is gone and there is
nothing to correct.

## Testing

- **`ProjectService`:** a taken name on create and on rename; both delete modes,
  asserting the chats returned to history in one and are gone in the other; move
  into a project and back out; the chat count produced by one query rather than
  one per project.
- **`ChatService`:** its existing history tests change — a chat with a project no
  longer appears in the history list.
- **`SearchService`:** `%`, `_` and the escape character each as their own case,
  because that is what breaks quietly; the 50-per-kind cap; message hits
  collapsing to one per chat; a blank term issuing no query; ordering within a
  kind.
- **bUnit:** the project section renders, collapses and expands; `ChatRow` behaves
  identically in both sections; the move dialog lists projects and "No project";
  the search modal's arrow keys and Enter move and open.
- **`ShortcutService`:** a subscriber receives `EscapePressed` while open and does
  not after closing.
- **Manual checklist** (`docs/web-ui.md`), because bUnit runs no browser:
  `Ctrl`/`Cmd`+`K` from inside a text field and from empty space; Escape closing
  the user menu in Safari after a mouse click; the browser's own `Ctrl`+`K` not
  firing over ours.

## Debts this phase closes

| Debt | From | How |
|---|---|---|
| Safari does not focus a button on mouse click, so Escape cannot close a menu opened by mouse | A, item 8 | The document-level listener this phase adds for `Ctrl`/`Cmd`+`K` |
| `ChatPageTests.A_dropped_send_still_tells_the_sidebar_a_chat_was_created` cannot fail | B1 | Snapshot the notification count immediately before releasing the gateway and assert it grew |
| `SqlAgent.Storage.Chat` collides with the `Chat` page component | B1 | Rename the page to `ChatPage.razor`; routes unchanged. Done now, while two test files carry the alias rather than twenty |
| Two Phase A tests do not test what their names claim | A, items 11 and 12 | `RestyleRegressionTests` inspects the compiled scoped-CSS output instead of a concatenation of every sheet, so a rule in the wrong sheet fails; `UiPrimitiveTests.No_icon_ships_that_nothing_renders` scans `*.razor` for the `Name="…"` values actually rendered instead of comparing against a hardcoded array it is kept in step with by hand |

Everything else in B1's carry-forward list stays carried, including the two open
decisions for the maintainer: copying the store before applying pending
migrations, and walking B1's manual checklist.

## Definition of done

- `dotnet build SqlAgent.slnx --configuration Release` is clean and
  `dotnet test SqlAgent.slnx --configuration Release` is green, pre-existing tests
  included.
- A chat moved into a project appears under it and no longer in history; moving it
  back restores it.
- Deleting a project offers both outcomes and performs the one chosen.
- A project name cannot be taken twice, and the dialog says so rather than
  failing silently.
- Search finds a chat by title, a chat by message text with a snippet, a project
  by name and a database by name; `50%` searches for a percent sign.
- `Ctrl`/`Cmd`+`K` opens search wherever focus is, and the browser's own shortcut
  does not fire with it.
- No component stylesheet contains a literal color.
- The four debts above are closed, and `docs/web-ui.md` documents projects,
  search, and the new manual checks.
