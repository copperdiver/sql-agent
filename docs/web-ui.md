# Web UI

The previous Windows-only desktop client is gone. The host (`SqlAgent.Host`) now serves a
Blazor Server web UI directly, bound to loopback only. There is nothing separate to launch —
start the host, open the URL it prints, and the browser is the client.

## Starting it

```bash
dotnet run --project src/SqlAgent.Host/SqlAgent.Host.csproj
```

On startup the host logs where to find the URL, but **not the URL itself**. What it logs, and
whether a file is involved at all, depends on where the token came from:

- **No `SqlAgent:LocalAuth:Token` configured (the default)** — a fresh token is generated for
  this process only, and the tokenized URL goes into `launch-url.txt` beside the SQLite store:

  ```
  SQL Agent UI: http://127.0.0.1:5099 — open the URL (token included) written to /var/lib/sqlagent/launch-url.txt
  ```

  The file is restricted to the account the host runs as (mode `600` on Linux/macOS; on Windows an
  explicit ACL granting only that account and `BUILTIN\Administrators`, with inheritance switched
  off). Read it and open the URL it contains:

  ```bash
  cat /var/lib/sqlagent/launch-url.txt
  ```

  ```powershell
  Get-Content "C:\ProgramData\SqlAgent\launch-url.txt"
  ```

  It is written fresh on every start and **removed automatically when the host shuts down** —
  a generated token is dead the moment the process exits, and there is no reason for its plaintext
  copy to outlive it. Deletion is best-effort: a file already gone, or locked by something else at
  that instant, does not stop the host from shutting down.

- **`SqlAgent:LocalAuth:Token` configured** — no file is written at all. The value is one the
  operator already holds (it also unlocks the MCP server — see below), so writing a second,
  indefinitely-lived plaintext copy of it to disk would cost confidentiality for no benefit; unlike
  a generated token it does not go stale on the next restart anyway. The host just logs the base
  URL and leaves it to the operator to append their own token:

  ```
  SQL Agent UI: http://127.0.0.1:5099 — open the URL with your configured SqlAgent:LocalAuth:Token appended as ?token=…
  ```

The token is **not** logged, at any level, on either path. With the named pipe gone it is the
entire trust boundary around a TCP port that every local account can reach, and this host attaches
log providers that are not private to it: `AddWindowsService()` writes to the Windows Event Log and
`AddSystemd()` puts stdout in the journal, both readable by a wider set of principals than the
service account. (As a second layer, `appsettings.json` also caps the Event Log provider at
`Warning`, so nothing logged at `Information` can reach it even if a future change tries.)

The `token` query parameter is required on the first request only; the server exchanges it for an
`HttpOnly` session cookie (`sqlagent_session`) and every request after that rides the cookie
instead. Opening the bare URL — `http://127.0.0.1:5099/` — without the token, in a window that never
presented one, gets a `401`.

If the file cannot be written (a read-only deployment directory, say) the host logs an error saying
so and still starts, but a *generated* token then has no retrieval path at all — set
`SqlAgent:LocalAuth:Token` to a value of your own instead, as described in the runbook.

See [`docs/runbook.md`](runbook.md) for where the token comes from, how to pin it to a fixed
value, and Windows service / systemd packaging.

## Port

`SqlAgent:Web:Port` (environment form `SqlAgent__Web__Port`) sets the TCP port; it defaults to
**5099**. Only the port is configurable — the bind address is not. The host always listens on
`127.0.0.1`, never `0.0.0.0` or a hostname, so the UI cannot be reached from another machine.
That is a deliberate v1 limit, not an oversight: there is no TLS and no multi-user session model
yet (both are tracked for a later phase), so anything beyond loopback would expose an
unauthenticated-by-default configuration surface to the network.

Loopback is not the same as "only this browser tab" though: **any** page open in the user's
browser can send a request to `127.0.0.1` — an HTTP port is reachable from arbitrary web
content in a way the local-only inter-process channel this UI replaced never was. Two checks
close that gap:

- **Host validation** — the request's `Host` header must be `127.0.0.1`, `localhost`, or
  `[::1]`. This blocks DNS-rebinding: a hostile domain that resolves to `127.0.0.1` cannot ride
  the browser's same-origin rules to reach the UI.
- **Origin validation** — when a request carries an `Origin` header, it must match this
  server's own scheme and authority *exactly*. A host-only check would still let a different
  local process (a dev server, another local app, anything else bound to loopback) open a
  request against this port; requiring the full origin means only a page actually served by
  this host is accepted. Requests with no `Origin` at all (ordinary top-level navigation) are
  allowed through — browsers don't send one for those.

Both checks run before authentication, so a request that fails them never even gets a chance to
try a token.

## The shell

The UI is a sidebar plus an inset main card. The sidebar carries the product mark, a collapse
toggle, the nav rows, the Databases, Projects and History sections, and the user card; the card
holds the current page.

- **Collapse** shrinks the sidebar to an icon rail. Below 1024px it leaves the layout entirely and
  becomes an overlay drawer opened from the hamburger at the top left.
- **The user card** shows the OS account and machine name. There is deliberately **no Sign out**:
  the host is single-user and loopback-only, the only session concept is the `sqlagent_session`
  cookie the launch token is exchanged for, and no user record exists to sign out of. Its menu
  offers Settings, Theme, and About.

## Themes

Three settings — system, light, dark — chosen from the segmented control in the user menu or on
`/settings`.

The choice lives in `localStorage` (`sqlagent.theme`), not in the SQLite store: it is a per-browser
preference, and a server round trip would paint the wrong theme first. `wwwroot/js/theme.js` is
loaded **synchronously from `<head>`** and applies the stored value to `<html>` before Blazor
connects; moving it to `<body>`, or adding `defer`, reintroduces a flash. `system` sets no class at
all, and `app.css` keys the OS preference off the absence of both classes so an explicit choice
always wins over the OS.

Colors are CSS custom properties in `wwwroot/css/app.css`; components consume `var(--token)` and
never a literal color. The dark palette is written twice — once for `:root.dark`, once inside
`@media (prefers-color-scheme: dark)` for the system setting — and
`DesignSystemTests.Every_token_redefined_for_dark_mode_is_also_redefined_for_the_system_preference`
pins the two blocks to the same property set so they cannot drift.

The sidebar's collapsed state is stored and applied the same way (`sqlagent.sidebar`, `html.sidebar-collapsed`).

### Known limitations

These are real gaps found during this phase's reviews, not undiscovered bugs — recorded here so
nobody spends time rediscovering them:

- **Escape does not close the user menu in Safari via mouse click.** Safari does not focus a
  `<button>` on a plain mouse click — this is a macOS platform convention (System Settings has a
  "Use keyboard navigation to move focus" toggle for it), not something unique to Safari as a
  rendering engine — and the menu's Escape handler relies on focus being inside it. Tabbing into
  the menu instead of clicking it works fine. There is no interop-free fix — reaching for one
  would mean adding JS just to work around this platform's focus model for one dismissal path.
- **The theme toggle shows "System" from first paint until the circuit connects, and can stay
  that way if interop never succeeds.** The app prerenders on the server, where there is no
  `localStorage` to read, so the toggle's Blazor-side state starts at the default ("System") and
  stays there for the whole prerendered window — not one frame, but however long it takes the
  circuit to connect, which on a slow or blocked connection is user-visible. If
  `sqlAgentUi.getTheme` never succeeds at all (blocked WebSocket, private-mode storage throwing,
  a disconnected circuit), `ThemeToggle.razor`'s `OnAfterRenderAsync` catches the failure, logs it
  at Debug, and leaves the control on "System" permanently — it does not retry. Either way this is
  only the *control's label* misreporting, not the page background: `theme.js` in `<head>` already
  applies the real color before first paint (see above), so a page that is actually dark with the
  toggle stuck on "System" is this known issue, not evidence the theme itself failed to apply.
- **The user menu backdrop and the About dialog misbehave on a narrow viewport.** Below 1024px the
  sidebar becomes a drawer positioned with a CSS `transform`, and a `transform` on an ancestor
  makes any `position: fixed` descendant resolve against that ancestor instead of the viewport.
  So the user menu's backdrop covers only the drawer (not the full screen), and the About dialog
  centers on the drawer, overhangs its edges, and can ride off-screen if the drawer closes while
  the dialog still considers itself open. Fixing this properly needs rendering those elements
  through a portal outside the transformed subtree; that is deferred rather than patched around.

## The screens

- **Databases** (`/database`, `/database/{id}`) — create, edit, test, and delete a database
  connection. Editing one never shows the stored connection string back; the field starts blank,
  and leaving it blank on save keeps the existing secret. Provider type and read-only mode are
  set here too. Opening a saved database also tests it and, on success, shows every live table
  and view with a three-level access control (hidden / read-only / full) — a view offers only
  hidden/read-only, since a view cannot be writable. A filter box narrows the list by name, and
  a level applies immediately, with no separate save step. The Structure permissions panel below it
  independently controls six DDL operations: Create table, Alter table, Drop table, Create index,
  Drop index, and Truncate table.
- **Chat** (`/`, `/chat/{id}`) — ask a question in plain English; the generated SQL and its
  result (or an error) appear in the transcript, with a button to open the generated SQL on the
  SQL page for editing. See "Chats, and what is kept" below for what persists across a reload
  and what deliberately does not.
- **SQL** (`/sql`) — a CodeMirror editor with SQL syntax highlighting, a result grid, CSV/JSON
  export, and Cancel for an in-flight query. The page has its own database picker at the top —
  the sidebar's Databases section links to the config page rather than selecting, so there is
  exactly one control on screen that decides which connection a query runs against.
- **Settings** (`/settings`) — three panels: appearance (the same theme control as the user menu),
  language-model status (whether `ILlmSqlGateway.IsConfigured` is true, with a badge), and
  environment (version, bind URL, port, store path, account — read from `HostInfo`).

## Access levels

Every table and view a connection can see has one of three access levels, set per object on that
database's config page (`/database/{id}`, in the objects panel) and enforced on the execution path
itself — `SqlPolicyValidator.Validate` checks it before a statement runs, not just before it renders:

- **Not visible** (`ObjectAccess.Hidden`) — absent from the schema handed to the model and to
  `describe_schema`. Naming it directly in SQL anyway is refused with `policy_denied_hidden_table`,
  and that check runs before any more specific one, over every table referenced (not only written
  ones) — a more specific refusal would concede the object exists.
- **Read-only** (`ObjectAccess.ReadOnly`) — visible and queryable; refused as a write target with
  `policy_denied_readonly_object`. A view can only be Not visible or Read-only, never Full — the
  objects panel doesn't even offer a view the third segment, and `TablePolicyService.SetAccessAsync`
  refuses a Full-access write for a view server-side too, in case a stale client tries anyway. A
  view being written to is refused before its level is even consulted, with the more specific
  `policy_denied_view_write` — the two codes can't both fire for the same statement.
- **Full access** (`ObjectAccess.Full`) — visible, queryable, and (tables only) writable.

## Structure permissions and confirmation

The Structure permissions panel is a per-connection allow-list. All six DDL switches default to off,
including on connections created before C2. The master switch selects or clears all six. Changing a
switch is saved immediately; it does not execute SQL and it does not bypass the connection's
read-only setting or per-object access levels.

The supported DDL operations are `CREATE TABLE`, `ALTER TABLE`, `DROP TABLE`, `CREATE INDEX`,
`DROP INDEX`, and `TRUNCATE`. A supported operation whose switch is off is refused with
`policy_denied_ddl`. Statements outside this closed set — including `CREATE VIEW`, routines,
`EXEC`, and `GRANT` — remain refused with `policy_denied_unsupported`.

Every write and permitted DDL statement also needs explicit confirmation at the execution boundary.
Typed SQL from `/sql` supplies that confirmation after the user presses Run. Natural-language Chat
and `query_database` MCP calls deliberately run unconfirmed: they return `ddl_confirmation_required`,
echo the generated SQL and operation, and do not call the provider. The chat transcript stores this
as a stable error-shaped outcome until the Phase D confirmation UI exists. Reads do not need this
extra confirmation.

**An object with no policy row is Full access — not Hidden, not Read-only.** This is the rule every
layer applies (see the resolver contract on `SqlPolicyValidator.Validate` and
`TablePolicyService.AccessOf`), and it is the one most likely to be misread, because the stored
`TablePolicy` entity's own `CanWrite` column **defaults to `false`** — which reads as "a new table
starts locked down." It means the opposite: a row in `TablePolicies` only exists once somebody has
set a level for that object from the config page, and until then the object is fully open. An empty
`TablePolicies` table for a connection is not "nothing configured yet, deny by default" — it is
"every table and view on this connection is unrestricted." Setting a schema's header control applies
a level to every object under it at once, clamping a view in the batch to Read-only rather than
failing the whole call if one member can't take the level requested.

**Upgrading from a store written before access levels existed.** The old schema rail could only hide
and un-hide an object, and its toggle wrote `IsVisible` alone — leaving `CanWrite` at the entity's
`false` default. Nothing read that column back then, so it did not matter. It does now: a row saying
"visible, not writable" is exactly Read-only. So an object you hid under the old rail and later
un-hid comes back as **Read-only**, not Full access, and a write to it is refused with
`policy_denied_readonly_object` until you say otherwise. One click on that object's Full access
segment in the Objects panel fixes it for good.

There is deliberately no migration for this. A legacy row and a Read-only level a user chose on
purpose in the new panel are the same two column values — the store cannot tell them apart — so any
migration that "restored" the legacy rows would silently unlock objects somebody had chosen to
protect. Refusing a write that should have been allowed is recoverable in one click; allowing a
write that should have been refused is not.

**`schema_unavailable`.** Telling a table from a view — which decides whether
`policy_denied_view_write` applies to a given write — needs the live schema, so a connection that can
run a query but cannot read its own catalog (a role granted `SELECT` but not the metadata views, for
instance) is now refused with `schema_unavailable` rather than executed, even when the query itself
would have been fine. Such a connection was already unusable for `describe_schema` and the whole
natural-language path, so this closes a gap rather than opening one — the alternative is a write to
what turns out to be a view slipping through because the policy check couldn't tell.

## Export

The Export CSV / Export JSON buttons on the SQL page format the rows already on screen — they
never re-run the query. CSV escapes commas/quotes/newlines, renders `byte[]` columns as base64
(the default `.ToString()` would otherwise produce the useless `System.Byte[]`), and formats
numbers and dates with `CultureInfo.InvariantCulture` so a file made on one machine reads
identically on another. JSON serialization gets the same base64/invariant behavior for free from
`JsonSerializer`. Files download through the browser's normal download mechanism, not a page
navigation.

Values are written verbatim, which means a cell whose text begins with `=`, `+`, `-`, or `@` may be
interpreted as a formula when the CSV is opened in a spreadsheet (CWE-1236). Mitigating it would
mean altering the user's own data on the way out, so it is a deliberate non-goal here rather than an
oversight — treat an exported CSV from an untrusted database the way you would treat any other
untrusted spreadsheet.

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
- **Files attach to a message** from the same composer menu. Selecting a file uploads it immediately
  into provider storage and shows a removable pending chip; the chip remains pending until the
  message is successfully persisted. Sent messages show read-only download chips with the original
  display name and size. File bytes are never put in SQLite, the chat transcript, or the LLM prompt.
  The server enforces a 25 MiB maximum per file and 10 files per message, regardless of picker
  attributes. The stable browser-facing failures are `file_too_large` and `file_rejected`; provider
  exception text is logged only on the host.
- **File downloads are authenticated and forced to download.** `GET /files/{id}` requires the
  existing session and resolves only the persisted attachment id. It sends
  `Content-Disposition: attachment`, `X-Content-Type-Options: nosniff`, and
  `Content-Security-Policy: sandbox`; HTML, XHTML, and SVG metadata is served as
  `application/octet-stream`, never inline. A missing id, missing blob, unauthenticated request, or
  provider failure does not disclose storage paths or exception details.
- **Result rows are never stored.** A reloaded answer shows its row count, duration and truncation flag
  with a note saying so; open the SQL in the editor and run it again to see the rows. This keeps the
  local store from becoming a shadow copy of production data.
- **Failed answers are stored too**, `llm_not_configured` among them. A reloaded conversation is the
  one the user watched, not a shorter edit of it. `llm_not_configured` is deliberately distinct from
  `llm_error`: the former means no provider is wired at all (today's state); the latter is reserved for
  a *configured* provider's own failures (timeout, malformed response, network error) once one exists.
  Only `llm_not_configured` gets the friendly panel — `llm_error` and every other chat error render
  through the same `OutcomeMessage` component the SQL page uses, showing the stable code and message.

The SQL editor has its own page at `/sql`. It is not going away: Phase D adds a scratchpad panel beside
the chat built from the same components.

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
project names and database names, grouped by kind — chats and messages newest first, projects and
databases alphabetically, the same order each already appears elsewhere in the app — and nothing
else: not SQL text, query results, table or column names, or connection details. Arrow keys move, Enter
opens, Escape closes.

A message match shows the text around it, and opens the chat at the top — matches are not scrolled to.
A project match opens that project in the sidebar; a database match opens that database's config page.

Wildcards are searched for literally: `50%` finds a percent sign, and `a_b` does not match `axb`.

## The store and its migrations

The SQLite store is versioned with EF Core migrations. A store created before this release has the
original tables and no `__EFMigrationsHistory`; startup stamps the initial migration as applied and then
migrates, so an existing store keeps its data. A migration that fails stops the host rather than running
against a half-migrated store — the log names the store path.

## File storage and model handoff

Phase E keeps file bytes behind `IFileStorageProvider`; SQLite stores only the message attachment
metadata needed to render a chip and resolve an authenticated download. The first provider is
`local-disk`, selected by `SqlAgent:Files:Provider` (environment form
`SqlAgent__Files__Provider`). Its root is a `files` directory beside the SQLite database: for the
default `Data Source=sqlagent.db`, that is `<current working directory>/files`; for an absolute
`SqlAgent:Storage:ConnectionString` data source, it is `<database directory>/files`. Each blob is
written under `yyyy/MM/{guid}{safe-extension}`. The client filename is display data only and never
chooses a directory or blob identity.

`SqlAgent:Files:MaxBytes` (`SqlAgent__Files__MaxBytes`) defaults to **25 MiB** (`26214400` bytes).
The message limit is 10 attachments (`FileStorageOptions.MaxAttachmentsPerMessage`). The picker is
only a convenience: the service streams and enforces the byte limit server-side, and the message
binding enforces the count limit. The host resolves `SqlAgent:Files:Provider` and
`SqlAgent:Files:MaxBytes` from configuration, using the documented defaults when they are unset. Only
the `local-disk` provider is registered in this release; changing the provider requires a provider
implementation and DI registration as described by
[`ADR 0006`](adr/0006-file-storage-provider-boundary.md).

The LLM boundary receives filename, content type, and authenticated URL metadata without opening or
copying file bytes into `LlmSqlRequest`. Those URLs are loopback-relative (`/files/{id}`), so a future
model hosted on this machine can fetch them through the same session, while a cloud model cannot reach
`127.0.0.1`; a remote/provider-backed URL and its own access-token design are required before cloud
file analysis is supported. The build intentionally has no real LLM provider yet.

Deleting a chat, or deleting a project with **Delete chats**, removes attachment metadata and asks the
owning provider to delete each blob best-effort. **Keep chats** leaves both metadata and blobs intact.
The host also runs an orphan sweep after migrations at startup: it only considers local-disk blobs
older than 24 hours with no matching metadata, so an abandoned upload newer than that age floor is not
removed while a live circuit could still be finishing its send.

## Manual regression checklist

CodeMirror is loaded and driven entirely through JS interop (`wwwroot/js/sql-editor.js`); bUnit
renders components against an in-memory test renderer with no browser and no JS engine, so it
cannot load CodeMirror, cannot observe syntax highlighting, and cannot verify Ctrl+Enter reaching
the editor's key handler. **The SQL editor is verified by this manual walkthrough only** — there
is no automated coverage for it beyond the C# side of the interop boundary
(`SqlEditorTests.cs`, which checks the component sends/receives values, not that CodeMirror
renders). File download similarly leaves the page through a browser API bUnit doesn't run.

Run this list by hand after any change to the web host, the Razor components, or the two JS
files under `wwwroot/js/`:

| Check | Expected |
|---|---|
| Open the URL from `launch-url.txt` | Chat loads |
| Navigate Chat → a database's config page → Chat via the sidebar nav | Both pages render and stay interactive; no full page reload |
| Create a database while Chat is open | It appears in the sidebar's Databases section without reloading the page |
| Open `http://127.0.0.1:5099/` with no token in a private window | 401 |
| Create a database, then test it | Version and elapsed time reported |
| Reopen the database for editing | Connection-string field is empty |
| Open a database's config page | Objects panel lists tables and views grouped by schema, each with a Not visible / Read-only / Full access control |
| Open a database's config page | Structure permissions shows six unchecked DDL operations by default, and the master switch selects/clears all six |
| Set a table to Not visible, run `SELECT` against it | `policy_denied_hidden_table` |
| Set the connection to read-only, run an `UPDATE` | `policy_denied_readonly` |
| Enable Drop table, ask Chat to drop a table | `ddl_confirmation_required`; no provider call is made |
| Run a typed UPDATE or permitted DDL from `/sql` | It runs only after the explicit Run action supplies confirmation |
| Type SQL, press Ctrl+Enter | Query runs, syntax is highlighted |
| Run a query returning more than 1000 rows | Truncation notice appears |
| Export CSV, then JSON | Both files download and open cleanly |
| Ask a question in Chat | "LLM is not configured" explanation, not a raw code |
| Use Chat Tools → Explain schema | The composer is prefilled without sending; the model state says "No model configured" until a provider exists |
| Use Chat Tools → Open scratchpad, or Edit on a SQL block | The SQL opens in CodeMirror; Run uses the same policy/audit boundary as `/sql` |
| Ask Chat for a write/DDL statement | The pending SQL survives reload and executes only after the confirmation dialog |
| Click Chat Tools → Schema diagram with one database attached | A policy-filtered ER diagram renders; zoom, fit, refresh, and SVG download work |
| Hide a table, then create/reload a schema diagram | The hidden table and its relationships are absent |
| Copy/edit a user message; copy/regenerate an assistant answer | Clipboard/edit actions work; regenerate replaces the existing answer rather than appending one |
| Start a slow query, press Cancel | `execution_canceled` |
| Set theme to Dark, reload | Page is dark on first paint — no white flash |
| Set theme to System, switch the OS between light and dark | Page follows the OS without a reload |
| Set theme to Light on a dark-mode OS | Page stays light — the explicit choice wins |
| Collapse the sidebar, reload | Sidebar renders collapsed on first paint, not wide-then-narrow |
| Narrow the window below 1024px | Sidebar becomes a drawer; the hamburger opens it; the scrim closes it, and so does Escape |
| Open the user menu, adjust the theme from its row | Theme changes and the menu stays open |
| Open About from the user menu | Version, bind URL, port, and store path are correct |
| Tab through the sidebar and the chat's question input | Focus ring is visible on every control, checkboxes included |
| Load the UI with `wwwroot/fonts/DMSans-Variable.woff2` removed | Text renders in the system sans-serif, not a serif |
| Ask a question, reload the page | Question, answer, SQL and the database chips all come back; the result grid does not, and says why |
| Open a new chat, type nothing, navigate away | No new row appears in the sidebar |
| Send with no database attached, then with two | Both explain themselves; both survive a reload |
| Attach a database, send twice | The chip is still there for the second question |
| Delete a connection that an old message used | The old message still shows the name it was sent with |
| Open the composer attachment menu → Files, pick a small file, then cancel the picker | The file picker opens; a selected file shows a pending chip; canceling leaves no chip and no sent message attachment |
| Pick an unreadable/failed upload or force a provider failure | The UI shows stable `file_rejected` copy without provider exception text; no unusable chip is persisted |
| Pick a file exactly 25 MiB, then one byte over 25 MiB | The exact-limit file uploads; the larger file is rejected with `file_too_large` |
| Attach 10 files, then try an 11th before sending | Ten files are accepted; the 11th is rejected with `file_rejected` and the existing pending chips remain usable |
| Send a message with a file, reload the chat, and click its file chip | The read-only chip persists with name/size; the authenticated link downloads the blob and does not navigate to its contents |
| Request an HTML or SVG attachment download | Response is `Content-Disposition: attachment`, `application/octet-stream`, `nosniff`, and CSP `sandbox`; no markup renders inline |
| Open a file link in a private/unauthenticated browser window | The existing session/auth boundary rejects it (401), and no blob details are disclosed |
| Delete a chat containing an attachment; repeat with a project and choose Delete chats | Attachment metadata disappears and the provider blob is deleted best-effort; provider cleanup failure does not block metadata deletion |
| Delete a project and choose Keep chats | Chat, attachment metadata, and blob remain available |
| Start an upload, abandon the send, restart the host, and inspect the files root | Pending state is gone after the circuit ends; startup cleanup removes only orphan blobs older than 24 hours, leaving newer files for the age-gated retry path |
| Rename and delete a chat from its `⋮` menu | Rename updates the row; delete asks first and names the chat |
| Do the same from inside the drawer below 1024px | The dialog centres on the viewport, not on the drawer, and survives the drawer closing |
| Tab through the page below 1024px with the drawer closed | Focus never enters the drawer |
| Open the drawer, close it with the scrim | Focus returns to the hamburger |
| Press Enter in the composer, then Shift+Enter | Enter sends; Shift+Enter adds a line and grows the box |
| Start the host against a store from before this release | It migrates, and the old connections are still listed |
| Press `Ctrl`/`Cmd`+`K` with focus in the composer, then with nothing focused | The search modal opens both times |
| Press `Ctrl`/`Cmd`+`K` in Chrome and Firefox | The browser's own shortcut does not fire — no address bar, no find bar |
| In Safari, open the user menu with the mouse and press Escape | The menu closes |
| Open a project, move a chat into it, reload | The chat is under the project and not in the history list |
| Delete a project holding a chat, choosing "keep the chats" | The chat is back in the history list |
| Search for a term that appears only in a message body | The result shows the surrounding text and opens the chat |
| Open search with `Ctrl`/`Cmd`+`K`, then again from the Search row, and start typing immediately each time | Characters land in the search input right away; bUnit cannot see this — Task 7's focus fix was found broken only in a real browser |
| Open **New project** and start typing immediately, with no click into the field first | Characters land in the name field right away; bUnit cannot see this either — Modal's own focus move is JS interop, invisible to any test in this suite the same way the search row above is |

## Approved scope that was consciously dropped

Two items the design spec described were left out of the build this note originally described.
One remains dropped and unscheduled; the other was partially delivered later and the record below
says by which phase, rather than being deleted now that it's stale:

- **Schema detail in the rail** *(the rail this note describes is gone — see **Databases** above
  for what replaced it)*. The spec called for a schema → table → column tree showing each column's
  declared type in full (`total numeric(10,2)`), PK/FK markers, and the table's indexes. What
  shipped at the time was a flat list of `schema.table` entries with a visibility checkbox and a
  name filter — no grouping by schema at all. **Phase C1's objects panel** (`/database/{id}`, which
  replaced the rail) delivered the schema grouping and marks each view with a badge, so the list is
  a schema → table tree today rather than a flat one. Column-level detail is still missing, though:
  no type, PK/FK marker, or index shows anywhere in the UI. `SchemaColumn.TypeText` and the key/index
  data are extracted and still reach the LLM — they are just never rendered for a person to read —
  so adding that detail later is still a rendering change, not a data change, and it is still not
  scheduled.
- **Copy SQL on `/sql`.** The editor remains the source of truth and does not duplicate a copy action;
  chat SQL blocks and assistant messages expose browser clipboard actions.

## Out of scope (tracked for later phases)

A portable secret store to replace Windows DPAPI, remote access with TLS and multi-user
sessions, an actual LLM provider wired behind `ILlmSqlGateway`, and voice input.
