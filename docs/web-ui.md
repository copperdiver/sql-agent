# Web UI

The previous Windows-only desktop client is gone. The host (`SqlAgent.Host`) now serves a
Blazor Server web UI directly, bound to loopback only. There is nothing separate to launch —
start the host, open the URL it prints, and the browser is the client.

## Starting it

```bash
dotnet run --project src/SqlAgent.Host/SqlAgent.Host.csproj
```

On startup the host logs a single ready-to-click line:

```
SQL Agent UI: http://127.0.0.1:5099/?token=<64 hex characters>
```

Open that exact URL. The `token` query parameter is required on the first request only; the
server exchanges it for an `HttpOnly` session cookie (`sqlagent_session`) and every request
after that rides the cookie instead. Opening the bare URL — `http://127.0.0.1:5099/` — without
the token, in a window that never presented one, gets a `401`.

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
| Open the logged URL | Workspace loads |
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

## Out of scope (tracked for later phases)

A portable secret store to replace Windows DPAPI, remote access with TLS and multi-user
sessions, an actual LLM provider wired behind `ILlmSqlGateway`, and voice input.
