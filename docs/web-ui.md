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

## The three screens

- **Connections** (`/connections`) — create, edit, test, and delete database connections.
  Editing a connection never shows the stored connection string back; the field starts blank,
  and leaving it blank on save keeps the existing secret. Provider type and read-only mode are
  set here too.
- **Workspace** (`/`) — the schema rail on the left plus two tabs:
  - **SQL** — a CodeMirror editor with SQL syntax highlighting, a result grid, CSV/JSON export,
    and Cancel for an in-flight query.
  - **Chat** — ask a question in plain English; the generated SQL and its result (or an error)
    appear in a running transcript, with a button to send the generated SQL to the SQL tab for
    editing.

  The rail lists every table for the selected connection with a visibility checkbox — unchecking
  one hides it from both the schema the SQL policy allows and the context given to the chat
  model. A filter box narrows the list by name.

## Export

The Export CSV / Export JSON buttons on the SQL tab format the rows already on screen — they
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

## Chat needs an LLM provider — and none ships configured

Every `ask_database` call up through the CD-51 contract terminates in a provider seam
(`ILlmSqlGateway`). The host wires a placeholder gateway that always fails closed — no LLM
vendor is set up in this build — so **every question asked on the Chat tab today returns
`llm_not_configured`**, and the tab shows an explanatory panel ("The LLM is not configured on
this server...") instead of the bare code. That is expected, not a bug: wiring a real provider
is out of scope for this phase (see `docs/runbook.md` when that lands).

`llm_not_configured` is deliberately distinct from `llm_error`: the former means no provider is
wired at all (today's state); the latter is reserved for a *configured* provider's own failures
(timeout, malformed response, network error) once one exists. Only `llm_not_configured` gets the
friendly panel — `llm_error` and every other Chat-tab error render through the same
`OutcomeMessage` component the SQL tab uses, showing the stable code and message.

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
| Open the URL from `launch-url.txt` | Workspace loads |
| Navigate Workspace → Connections → Workspace via the header | Both pages render and stay interactive; no full page reload |
| Create a connection while the Workspace is open | It appears in the rail's picker without reloading the tab |
| Open `http://127.0.0.1:5099/` with no token in a private window | 401 |
| Create a connection, then test it | Version and elapsed time reported |
| Reopen the connection for editing | Connection-string field is empty |
| Select the connection | Rail lists tables with checkboxes |
| Uncheck a table, run `SELECT` against it | `policy_denied_hidden_table` |
| Set read-only, run an `UPDATE` | `policy_denied_readonly` |
| Type SQL, press Ctrl+Enter | Query runs, syntax is highlighted |
| Run a query returning more than 1000 rows | Truncation notice appears |
| Export CSV, then JSON | Both files download and open cleanly |
| Ask a question on the Chat tab | "LLM is not configured" explanation, not a raw code |
| Start a slow query, press Cancel | `execution_canceled` |

## Approved scope that was consciously dropped

Two items the design spec described are **not** in this build. Both were left out on purpose, not
missed, and neither is scheduled:

- **Schema detail in the rail.** The spec called for a schema → table → column tree showing each
  column's declared type in full (`total numeric(10,2)`), PK/FK markers, and the table's indexes.
  What shipped is a flat list of `schema.table` entries, each with the visibility checkbox and the
  name filter. The rail's job here is configuration — deciding what the agent may see — and the
  checkbox is what does that; column detail is a browsing feature that the SQL tab already covers by
  querying. `SchemaColumn.TypeText` and the key/index data are all still extracted and still reach
  the LLM, so adding the detail later is a rendering change, not a data change.
- **Copy SQL.** The spec listed copy-SQL alongside export CSV/JSON on the SQL tab. There is no such
  button: the editor holds the text and the browser's own selection and clipboard already do the
  job, whereas a copy button needs clipboard interop and a permissions story of its own. The chat
  tab's "open in editor" covers the one case where the SQL is somewhere the user cannot easily
  select it.

## Out of scope (tracked for later phases)

A portable secret store to replace Windows DPAPI, remote access with TLS and multi-user
sessions, an actual LLM provider wired behind `ILlmSqlGateway`, and voice input.
