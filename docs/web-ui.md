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
toggle, the nav rows, the schema rail, and the user card; the card holds the current page.

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

- **Connections** (`/connections`) — create, edit, test, and delete database connections.
  Editing a connection never shows the stored connection string back; the field starts blank,
  and leaving it blank on save keeps the existing secret. Provider type and read-only mode are
  set here too.
- **Chat** (`/`, `/chat/{id}`) — ask a question in plain English; the generated SQL and its
  result (or an error) appear in the transcript, with a button to open the generated SQL on the
  SQL page for editing. See "Chats, and what is kept" below for what persists across a reload
  and what deliberately does not.
- **SQL** (`/sql`) — a CodeMirror editor with SQL syntax highlighting, a result grid, CSV/JSON
  export, and Cancel for an in-flight query.

  The sidebar's schema rail, shared by both pages, lists every table for the selected connection
  with a visibility checkbox — unchecking one hides it from both the schema the SQL policy
  allows and the context given to the chat model. A filter box narrows the list by name.
- **Settings** (`/settings`) — three panels: appearance (the same theme control as the user menu),
  language-model status (whether `ILlmSqlGateway.IsConfigured` is true, with a badge), and
  environment (version, bind URL, port, store path, account — read from `HostInfo`).

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
A project match opens that project in the sidebar; a database match goes to Connections.

Wildcards are searched for literally: `50%` finds a percent sign, and `a_b` does not match `axb`.

## The store and its migrations

The SQLite store is versioned with EF Core migrations. A store created before this release has the
original tables and no `__EFMigrationsHistory`; startup stamps the initial migration as applied and then
migrates, so an existing store keeps its data. A migration that fails stops the host rather than running
against a half-migrated store — the log names the store path.

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
| Navigate Chat → Connections → Chat via the sidebar nav | Both pages render and stay interactive; no full page reload |
| Create a connection while Chat is open | It appears in the rail's picker without reloading the page |
| Open `http://127.0.0.1:5099/` with no token in a private window | 401 |
| Create a connection, then test it | Version and elapsed time reported |
| Reopen the connection for editing | Connection-string field is empty |
| Select the connection | Rail lists tables with checkboxes |
| Uncheck a table, run `SELECT` against it | `policy_denied_hidden_table` |
| Set read-only, run an `UPDATE` | `policy_denied_readonly` |
| Type SQL, press Ctrl+Enter | Query runs, syntax is highlighted |
| Run a query returning more than 1000 rows | Truncation notice appears |
| Export CSV, then JSON | Both files download and open cleanly |
| Ask a question in Chat | "LLM is not configured" explanation, not a raw code |
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

Two items the design spec described are **not** in this build. Both were left out on purpose, not
missed, and neither is scheduled:

- **Schema detail in the rail.** The spec called for a schema → table → column tree showing each
  column's declared type in full (`total numeric(10,2)`), PK/FK markers, and the table's indexes.
  What shipped is a flat list of `schema.table` entries, each with the visibility checkbox and the
  name filter. The rail's job here is configuration — deciding what the agent may see — and the
  checkbox is what does that; column detail is a browsing feature that the SQL page already covers by
  querying. `SchemaColumn.TypeText` and the key/index data are all still extracted and still reach
  the LLM, so adding the detail later is a rendering change, not a data change.
- **Copy SQL.** The spec listed copy-SQL alongside export CSV/JSON on the SQL page. There is no such
  button: the editor holds the text and the browser's own selection and clipboard already do the
  job, whereas a copy button needs clipboard interop and a permissions story of its own. The chat
  page's "open in editor" covers the one case where the SQL is somewhere the user cannot easily
  select it.

## Out of scope (tracked for later phases)

A portable secret store to replace Windows DPAPI, remote access with TLS and multi-user
sessions, an actual LLM provider wired behind `ILlmSqlGateway`, and voice input.
