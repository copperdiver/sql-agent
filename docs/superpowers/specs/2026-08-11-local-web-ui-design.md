# Local web UI replacing the WPF client

- **Status:** approved, ready for implementation planning
- **Date:** 2026-08-11
- **Scope:** phase 1 of 3 (see [Phasing](#phasing))

## Context

`SqlAgent.Client.Wpf` is the only user-facing client. It talks to `SqlAgent.Host`
over a local named pipe using the DTOs in `SqlAgent.Api.Local.Contracts`. Three
properties of that arrangement are now costing more than they return:

- **Windows-only.** WPF pins the client to Windows. The rest of the solution is
  cross-platform.
- **Untestable in CI.** The test project cannot reference a `net10.0-windows`
  project without breaking `dotnet restore` on the Linux agent, so the client has
  no automated coverage at all — only the manual checklist in `docs/wpf-client.md`.
  Commit `57ece40` exists solely to keep the solution restorable on Linux.
- **Expensive to enrich.** A SQL editor with syntax highlighting, a schema
  browser, and result export are all disproportionately costly in WPF.

The client is also missing `ask_database`: its chat tab sends raw SQL through
`execute_sql`, so the natural-language path that Core already implements has no
user-facing surface.

## Goals

Replace the WPF client with a web UI served on loopback by the daemon, at parity
with the WPF feature set plus the natural-language path, a SQL editor with
highlighting, a schema browser, and result export.

## Non-goals

Explicitly out of scope for this phase, each with a reason:

| Out of scope | Why |
|---|---|
| Portable secret store (replacing DPAPI) | Phase 2. Independent of the UI; the web UI runs on Windows with the current store. |
| Remote access (TLS, sessions, non-loopback binding) | Phase 3. Requires phase 2 first, and hardening an unbuilt UI is wasted work. |
| Wiring a real LLM provider | Independent task. The chat UI is built now and needs no change when the provider lands. |
| Voice input | The WPF implementation was a stub that never worked. Not part of replacing a client. |
| A public HTTP/REST API | Nothing consumes it. MCP already covers programmatic access. |

## Phasing

1. **This spec** — web UI on `127.0.0.1`, WPF and the named pipe removed.
2. **Portable secret store** — replace Windows DPAPI so Linux and macOS get
   durable connection secrets.
3. **Remote access** — TLS, real sessions, non-loopback binding. Depends on 2.

## Architecture

`SqlAgent.Host` becomes an ASP.NET Core application hosting Blazor Server
components. One process owns the SQLite store and serves the UI; splitting the
web app into its own process would put two writers on one SQLite file for no
benefit. Service hosting is preserved — `AddWindowsService()` and `AddSystemd()`
work with `WebApplication` as they do with the current generic host.

### Stack decision

Blazor Server was chosen over a Minimal API + SPA and over Razor Pages + htmx.

- The codebase is 100% .NET with zero JavaScript. A Node toolchain in the build
  and in CI is a permanent tax for four screens.
- Blazor components call the existing `SqlAgent.Storage` services directly, so no
  second contract layer appears between UI and Core.
- bUnit gives real component tests on the Linux CI agent, which directly serves
  the "testable" goal that WPF never met.
- An SPA would win if a third party consumed the HTTP API. None does; MCP fills
  that role. Phase 3 works over a Blazor circuit as well as over REST.

CodeMirror is the single JavaScript dependency, vendored into the repository as
pre-built files and driven through `IJSRuntime`. It is not loaded from a CDN: a
local agent must work offline, and a CDN would leak usage to a third party.

### Project changes

| Project | Change |
|---|---|
| `SqlAgent.Host` | Becomes a web application; hosts the Blazor components |
| `SqlAgent.Api.Local` | Deleted |
| `SqlAgent.Api.Local.Contracts` | Deleted |
| `SqlAgent.Client.Wpf` | Deleted |
| `SqlAgent.Core`, `SqlAgent.Storage`, both providers, `SqlAgent.Api.Mcp` | Unchanged |

Deleting the two API projects removes `LocalApiDispatcherTests` (22 tests) and
`NamedPipeApiServerTests` (1 test): **23 tests, not the 18 quoted during the
design discussion.** The behaviors they covered — error codes, DTO shapes,
policy denials — originate in Core and Storage and keep their own tests; what is
lost is coverage of a transport that no longer exists. New component, HTTP, and
export tests are expected to land in roughly the same number, so the suite total
should stay near its current 144. That is a replacement, not growth.

## Security model

Moving the configuration surface from a named pipe to HTTP is not neutral. A pipe
is reachable only by processes of the same user and never by a browser. A
loopback HTTP port **is** reachable from any web page the user has open, so a
naive port of the transport would weaken the posture rather than preserve it.

Three measures, all in phase 1:

1. **Loopback binding only.** The listener binds `127.0.0.1` explicitly — not
   `0.0.0.0`, not a `localhost` wildcard that may resolve to a routable
   interface. Port configurable via `SqlAgent:Web:Port`, default **5099**.
2. **`Host` and `Origin` validation.** Middleware ahead of everything else
   rejects requests whose `Host` is not the configured loopback authority or
   whose `Origin` is foreign. This applies to the Blazor WebSocket negotiation as
   well as ordinary requests, and is what closes DNS rebinding.
3. **Launch token.** The host logs a URL of the form
   `http://127.0.0.1:5099/?token=…`. Presenting a valid token exchanges it for a
   session cookie; afterwards the token is not needed in the URL. The existing
   `LocalTokenAuthenticator` supplies the expected value when the operator
   configured `SqlAgent:LocalAuth:Token`; otherwise a random token is generated
   per start.

   The generated token **must stay in memory and must not be written to the
   secret store.** `LocalTokenAuthenticator.ConfigureFromSettingAsync` persists
   whatever it is given, and a blank setting does not clear a stored value — so
   persisting a per-start token would silently turn on authentication forever for
   the MCP server too, with a value that changes on every restart. The operator's
   configured token keeps its current persisted behavior; only the generated
   fallback is in-memory.

Requests without a valid session cookie get 401, including WebSocket upgrades.

## Screens

### Persistent left rail

- Connection picker at the top, showing provider and the read-only flag.
- Schema tree below: schema → table → columns. Columns show the declared type in
  full (`total numeric(10,2)`, from `SchemaColumn.TypeText`), plus PK/FK markers
  and the table's indexes.
- A visibility checkbox on every table. This absorbs the WPF "Tables" tab
  entirely — the toggle lives next to the table name.
- Name search above the tree; a schema with hundreds of tables is unusable
  without it.

**Filtered vs unfiltered schema.** The tree is a configuration surface, so it
lists every live table including hidden ones — otherwise a hidden table could
never be restored. Hidden tables render dimmed. Everything that feeds the agent
stays filtered: `SchemaService` continues to omit hidden tables from the schema
handed to the LLM and to `describe_schema`.

### Connections page

Reached from the header. List, create, edit, delete, and test. Test reports
server version and elapsed time. The connection-string field stays write-only: a
blank value on edit keeps the stored secret, matching current behavior.

### SQL tab

CodeMirror editor with syntax highlighting, line numbers, and Ctrl+Enter to run.
Below it: the result grid, row count, elapsed time, and a truncation notice when
the 1000-row cap trims the result. Actions: copy SQL, export CSV, export JSON.
Export serializes the rows already on screen — it does not re-query.

A cancel button is available while a query runs. `QueryExecutionService` already
distinguishes caller cancellation (`execution_canceled`) from timeout
(`execution_timeout`); WPF simply had no way to trigger the former.

### Chat tab

A natural-language question calls `NlQueryService.AskAsync`. The three outcomes
render differently:

- **query_result** — generated SQL plus the result grid.
- **clarification_required** — the question text alone; no SQL ran.
- **error** — code and message, still showing the generated SQL when one existed,
  so the user can see what was rejected.

Generated SQL carries an "open in editor" action that moves it to the SQL tab for
manual editing.

Because no LLM provider is wired yet, `ask_database` currently always returns
`llm_not_configured`. The tab detects that code specifically and shows an
explanatory "LLM not configured" state with a documentation link instead of a raw
error code. No UI change is needed when a provider is added.

`llm_not_configured` is deliberately distinct from `llm_error`. This spec
originally used `llm_error` for both, but `NlQueryService` mapped *any* gateway
exception to it, so once a real provider was wired its timeouts and network
failures would have been reported to the user as "no LLM configured".
`llm_error` now means a configured provider's own call failed, and renders
through the ordinary `OutcomeMessage` path with its code like every other error.

## Data flow

### DbContext lifetime

A Blazor Server circuit lives as long as the browser tab — hours. Scoped services
are bound to the circuit, so a single `SqlAgentDbContext` would persist for that
whole time, accumulating tracked entities and serving stale reads. Every
`SqlAgent.Storage` service currently takes `SqlAgentDbContext` by constructor
injection.

**Resolution:** each user action opens its own DI scope, resolves the service it
needs, runs the operation, and disposes the scope. A small helper in the web
layer (`ScopedRunner`, roughly ten lines over `IServiceScopeFactory`) encapsulates
this. Converting all services to `IDbContextFactory` would reach the same result
by editing seven classes and their tests; the scope-per-action approach leaves
Storage untouched.

### Representative flow

Running a query: component → `ScopedRunner` → new scope →
`QueryExecutionService.ExecuteSqlAsync(connectionId, sql, ct)` → policy
validation, execution under timeout and row cap, and one audit row all happen
inside Core → `QueryExecutionResult` returns with either rows or an error code →
component renders a grid or a message. No new logic is introduced between the UI
and Core.

Other actions follow the same shape:

| Action | Service call | Note |
|---|---|---|
| Toggle table visibility | `TablePolicyService.SetVisibilityAsync` | Already clears the cached schema internally; the UI only re-reads the tree |
| Ask a question | `NlQueryService.AskAsync` | |
| Save/delete/test a connection | `DatabaseConnectionService`, `ConnectionTester` | |
| Refresh schema | `SchemaService.RefreshAsync` | |

## Error handling

Two classes, handled differently.

**Expected outcomes are values, not exceptions.** Policy denials, timeouts, a
missing secret, and an unconfigured LLM arrive as results carrying stable codes.
The UI renders them as ordinary content — message text with the code shown
smaller alongside. The user must be able to tell "this was deliberately refused"
from "the app broke".

**Unexpected failures are exceptions.** An `ErrorBoundary` around the work area
catches them: the rest of the UI keeps working, the user sees a retry prompt, and
details go to the server log. Exception text is never rendered — it can contain a
connection string.

Circuit disconnects reconnect automatically, so tab state survives a laptop
sleeping.

## Testing

| Layer | Tool | What it covers |
|---|---|---|
| Components | bUnit | Hidden tables render dimmed; toggling a checkbox calls the service exactly once; the grid shows the truncation notice; a policy denial renders as a message with its code, not an exception; the chat tab shows "LLM not configured" rather than `llm_error` |
| HTTP surface | `WebApplicationFactory` | No cookie → 401; a valid token issues a cookie; an invalid one does not; a foreign `Host` or `Origin` is rejected |
| Binding | Plain unit test over the startup configuration | The resolved listen URL is a `127.0.0.1` authority. `WebApplicationFactory` runs on an in-memory `TestServer` and opens no socket, so it cannot observe the real binding — the assertion has to sit on the configuration that produces it |
| Export | Plain unit tests | CSV/JSON generation: quote and comma escaping, `NULL` vs empty string, duplicate column names |

The HTTP tests matter more than the component tests: they are the regression
guard on the security model described above.

All three layers run on the existing Linux CI agent.

## CI, packaging, and documentation

- The solution loses its only `net10.0-windows` project, and with it the reason
  for commit `57ece40`. CI job definitions need no change — they already build
  the whole solution.
- `packaging/windows/install-service.ps1` and `packaging/systemd/sqlagent.service`
  gain the port setting.
- `docs/wpf-client.md` is replaced by `docs/web-ui.md`.
- `docs/runbook.md` gains the URL, port, and launch-token section.
- `README.md` gains instructions for opening the UI.
- MCP and IDE documentation is untouched; the MCP server is unaffected.

## Risks

| Risk | Mitigation |
|---|---|
| The loopback HTTP surface is browser-reachable in a way the pipe was not | Host/Origin validation plus the launch token, both in phase 1 and both covered by tests |
| A schema with hundreds of tables makes the tree slow | Name search in phase 1; virtualization if it proves necessary |
| Deleting the transport removes 23 tests | Underlying behavior keeps its coverage in Core and Storage; new tests target previously uncovered ground |
| Blazor circuit state is server-side | Acceptable for a single-user local tool; revisit in phase 3 |
