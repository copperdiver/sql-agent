# Web UI Phase C1: databases, objects, and per-object access

- **Status:** approved, ready for implementation planning
- **Date:** 2026-08-14
- **Depends on:** Phase A, Phase B1, Phase B2 (all shipped)
- **Parent spec:** [2026-08-12 web UI overhaul](2026-08-12-web-ui-overhaul-design.md)
- **Predecessor:** [2026-08-13 phase B2](2026-08-13-web-ui-phase-b2-projects-and-search-design.md)
- **Successor:** phase C2, not yet designed

## Context

The parent spec describes phase C as one block: a Databases sidebar section, a
config page with three panels, view extraction in both providers, per-object
access control, DDL classification with a permissions flag set, and a
confirm-before-run flag threaded through the execution services. That is more
work than B1 and B2 carried together, and it divides on a clean seam, so C is
split the way B was.

**C1 — this spec — answers "which objects can the agent see, and which of them
can it write to."** **C2 answers "which shapes of statement are allowed at all,
and how they are confirmed."** C2 needs C1's page to hang its third panel on,
and its user-facing half — the confirm dialog in chat — belongs to phase D
anyway, where `OutcomeKind.ConfirmationRequired` lands. So C2 is close to
entirely backend.

C1 is also where two surfaces that survived phase A untouched finally go.
`Connections.razor` is still raw `<h1>`/`<table>`/`<label>` markup with no design
system, and `SchemaRail` is still the only place a table can be hidden. Both are
replaced here, as the parent spec always intended.

The model itself remains unwired and stays unwired.

## Goals

1. A Databases section in the sidebar and a `/database/{id?}` config page that
   replaces `/connections` and the schema rail.
2. Views extracted from both providers, carried through `DatabaseSchema`, and
   filtered by the same visibility rule tables already obey.
3. A three-way access level per object, enforced on the execution path.
4. Connection-test failures classified into a fixed set of safe outcomes, so no
   driver text reaches the browser.
5. Close the three carried debts listed under [Debts](#debts-this-phase-closes).

## Non-goals

| Out of scope | Why |
|---|---|
| Panel 3, DDL classification, `AllowedDdl`, `policy_denied_ddl`, the `confirmed` flag, `ConfirmationRequired` | This is C2. The seam is deliberate: C1 is about objects, C2 about statement shapes. |
| Procedures, functions, sequences | Unchanged from the parent spec: routine catalogs diverge sharply between the two engines, and granting one to the agent means allowing `EXEC`, which stays denied. |
| View definitions (`CREATE VIEW` bodies) | Column lists are what query generation needs. Definitions are unbounded text headed for a prompt. |
| Materialized views | Unchanged from the parent spec. |
| Virtualization in the Objects panel | Schema groups are collapsed by default and a name filter is always present, which bounds what renders without a component that has its own scroll-position bugs. If a real database makes the panel unusable, that is a measured finding, not a guess made here. |
| Removing `TablePolicy.CanRead` | The level table never produces `CanRead = false`, so the column stops varying (see [Entities](#entities)). Removing it is a migration bought for nothing; it is recorded as a candidate instead. |
| A per-engine glyph, which phase A deferred to "the phase that builds the database list" | The icon set is a hand-drawn dictionary of geometric paths, and an engine mark recognizable as SQL Server or Postgres is that vendor's trademark. Drawing something close enough to read as one is worse than not drawing it. The row uses the shared `database` glyph and names the engine in a `Badge`, which is also what the config page's DBMS select says. |
| Background connection polling | The status dot reflects tests performed in this session, as the parent spec says. A poller against arbitrary remote servers is a feature nobody asked for. |
| Persisting which schema groups are expanded | Same reasoning B2 applied to expanded projects: another `localStorage` key for a state one click restores. |
| The remaining B1 and B2 carried items | Listed in the B2 plan and still carried. Only the three named below are in scope. |

## Decisions taken before design

| Decision | Alternative rejected | Why |
|---|---|---|
| An object with no `TablePolicy` row is **Full access** | Read-only, fail-closed | The project's documented rule is already "no policy row defaults to visible", stated in `SchemaService`, `TablePolicyService`, and `SqlPolicyValidator`. Making write the one axis that fail-closes on absence would both contradict that rule and silently turn every existing writable connection read-only. The connection's own read-only flag remains the coarse gate, and C2 adds confirmation on top of every model-generated write. |
| "This object is a view" comes from the cached filtered schema | `TablePolicy.ObjectKind`; a live catalog read per query | Given the decision above, a view the user never touched has no policy row, so the policy table cannot answer the question. A live read per query is exactly what `SchemaCache` exists to avoid. |
| `ParsedStatement` gains `WrittenTables` | Denying a write when *any* referenced object is Read-only | Without roles, `INSERT INTO writable SELECT * FROM readonly_lookup` is indistinguishable from `INSERT INTO readonly_lookup SELECT * FROM writable`. Fail-closed on the flat list makes the Read-only level toxic: it would forbid reading a lookup table inside any write. |
| One resolver returning a policy record, not three predicates | `isVisible` + `canWrite` + `isView` | Three predicates means three copies of the fail-closed unqualified-name rule. One lookup, one rule, one place to get it wrong. |
| The connection picker moves onto `/sql` | A picker in the Databases section; a connection in the `/sql` route | `AppState.Connection` has exactly two readers today, the rail and `/sql`. With the rail gone, `/sql` is the only one, so the state and its control belong together. A section row that both navigates and selects would carry two meanings; a `/sql/{id?}` route is an edit to a B1 page beyond what phase C claims. |
| Connection-test failures are classified into safe kinds | Rendering the provider's text; a single fixed sentence | The parent spec forbids rendering provider exception text; the shipped code renders it anyway, behind a comment asserting it is "a deliberately returned domain result, not exception text" — which `PostgresProvider.cs:23` contradicts by passing `ex.Message` straight through. A single fixed sentence obeys the rule but makes a config page whose whole job is getting a connection working diagnose nothing. |
| The Databases nav row survives, retargeted | Dropping it, as projects and history have none | `sidebar-body` is hidden entirely when the sidebar is collapsed to the icon rail, and `theme.js` restores the collapsed state before first paint. Dropping the row leaves a fresh install with a collapsed sidebar no path to "add a database" at all. |
| `TablePolicy` does **not** gain `ObjectKind` | Adding it, as the parent spec directs | The parent spec adds the column "for display and filtering". Neither materializes: the page reads an object's kind from `TablePolicyService.ListAsync`, which must derive it from the live schema because objects with no policy row have no column to read, and the policy path reads views from the cached schema by the decision above. The column would be written by one path and read by none — the exact fault this spec calls out in `CanRead` two sections later, against the project's own rule that nothing lives in the codebase that nothing renders. |

## Architecture

Work spans `SqlAgent.Core`, `SqlAgent.Storage`, both providers, and
`SqlAgent.Host`. No new project.

### Core: views

```csharp
public record DatabaseSchema(
    IReadOnlyList<SchemaTable> Tables,
    IReadOnlyList<SchemaView>? Views = null)
{
    public IReadOnlyList<SchemaView> ViewList => Views ?? [];
}

public record SchemaView(string Schema, string Name, IReadOnlyList<SchemaColumn> Columns);
```

A defaulted trailing parameter, so no existing call site breaks and JSON written
by an earlier build deserializes with `Views = null`. `SchemaModel.Build` gains
an optional view-row parameter in the same flat shape its column rows already
use; `SchemaModel.Filter` applies the same visibility predicate to views.
`SchemaView` carries no foreign keys or indexes — a view has neither.

Both providers gain an object-type discriminator and select view columns from the
column catalog they already query: `INFORMATION_SCHEMA.TABLES.TABLE_TYPE` on SQL
Server, where the existing `WHERE t.TABLE_TYPE = 'BASE TABLE'` becomes a split
rather than a filter, and `pg_class.relkind` on Postgres. Postgres keeps its
existing user-schema exclusion so catalog and temp objects stay out.

### Core: statement roles

`ParsedStatement` gains `WrittenTables` beside `Tables`. `Tables` keeps its
present meaning — every object the statement touches — so the visibility check
does not change by a line. `TableCollector` already lifts the `INSERT` target out
of its own branch, because an insert target is a bare `ObjectName` rather than a
`TableFactor`; the `UPDATE` and `DELETE` targets are added the same way.

The write set must survive the forms that hide a target behind other syntax:
`UPDATE ... FROM`, `DELETE` with an alias, and a target wrapped so that the same
name also appears as a read source. CTE scoping is unchanged — a name resolved to
a CTE alias is not an object and reaches neither list.

### Core: policy

`SqlPolicyValidator.Validate` today takes `Func<SqlTableReference, bool> isVisible`.
It takes one resolver instead:

```csharp
public enum ObjectAccess { Hidden, ReadOnly, Full }

public record ObjectPolicy(ObjectAccess Access, bool IsView);

PolicyDecision Validate(
    string sql,
    DatabaseProviderType provider,
    bool isReadOnly,
    Func<SqlTableReference, ObjectPolicy> resolve);
```

Resolution of an unqualified name generalizes the rule already in force and stays
fail-closed: a bare name takes the **most restrictive** access among same-named
objects across every schema (`Hidden` > `ReadOnly` > `Full`), and counts as a view
if **any** same-named object is a view. A schema-qualified name matches on schema
too. The consequence is worth stating plainly: an unqualified name that matches a
table in one schema and a view in another is treated as a view, and a write to it
is refused. That is the same trade the hidden-table rule has always made.

Denial order:

1. empty, parse error, multi-statement — unchanged
2. `policy_denied_unsupported` — unchanged
3. `policy_denied_readonly` (the connection's own flag) — unchanged
4. `policy_denied_hidden_table`, over `Tables` — unchanged
5. `policy_denied_view_write` — a written object that is a view
6. `policy_denied_readonly_object` — a written object at `ObjectAccess.ReadOnly`

Five before six is load-bearing. Views can only be Not visible or Read-only, so
in the other order every write to a view would report as a level denial and
`policy_denied_view_write` would be unreachable code.

### Entities

`TablePolicy` is unchanged in shape. The parent spec adds an `ObjectKind` column;
this phase does not, for the reason recorded under
[Decisions](#decisions-taken-before-design) — nothing would read it. An object's
kind is derived from the live schema wherever it is needed: by
`TablePolicyService.ListAsync` for the page, and from the cached schema by the
policy path. The existing unique index on
`(DatabaseConnectionId, SchemaName, TableName)` is therefore untouched, and still
holds for the reason the parent spec gives: neither engine lets a table and a view
share a name within a schema.

The three levels map to the existing columns exactly as the parent spec says:

| Level | `IsVisible` | `CanRead` | `CanWrite` |
|---|---|---|---|
| Not visible | false | — | — |
| Read-only | true | true | false |
| Full access | true | true | true |

`CanRead` is therefore never written `false` by any path. It stops varying, which
makes it a column nothing reads for a decision — the same objection that keeps
`ObjectKind` out. The two are treated differently because the cost is not
symmetric: `ObjectKind` does not exist, and adding it means paying a schema change
for a column with no reader, while `CanRead` already exists, and dropping it means
paying SQLite's full table rebuild to delete something inert. It is recorded here
as a candidate for whichever later phase rebuilds `TablePolicy` for its own
reasons, so the next reader does not mistake it for a live axis.

One migration, and it changes no schema: it clears `SchemaCache` rows. That is not
tidiness. Cached JSON written by an earlier build carries no views, so without the
clear a view would silently never reach the model until some unrelated policy
change happened to invalidate the cache. A data-only migration is the honest place
for it — the cache's *format* changed with this phase, and a migration is what
records "everything written before this point is no longer complete".

The alternative — a format-version column on `SchemaCache` that
`GetOrRefreshAsync` checks, so a stale-format row is rejected rather than
explicitly deleted — is the general solution and is not built here. One
recurrence is not yet a pattern, and it would trade a migration nobody has to
think about for a check on the hot read path forever.

### Services

`TablePolicyService` grows a second dimension:

- `TableVisibility` becomes a record carrying schema, name, object kind, and
  `ObjectAccess`.
- `ListAsync` returns tables **and** views, each with its effective level.
- `SetVisibilityAsync` becomes `SetAccessAsync(connectionId, schema, name, kind, access)`,
  upserting a row on every call — including for `Full`, so the panel's state is
  explicit in the store rather than inferred from a row's absence. `kind` is an
  argument, not a stored column: its only job is the refusal below. It refuses
  `Full` for a view with a returned outcome rather than a `bool`, following the
  rule B2 set for `ProjectService`. The panel never offers that level, so the
  refusal exists to keep the service correct independent of its caller.
- `SetSchemaAccessAsync(connectionId, schema, access)` sets every object under one
  schema, applying the same view restriction per object rather than failing the
  batch.
- Every write path invalidates `SchemaCache`, exactly as the single toggle does
  today.

`QueryExecutionService` gains a `SchemaService` dependency and builds the resolver
from two reads: all `TablePolicy` rows for the connection (not only the hidden
ones, since the level now matters), and `SchemaService.GetOrRefreshAsync` for the
view set. That the cached schema is already visibility-filtered does not weaken
the view check: a hidden object is denied at step 4, before step 5 asks anything.
On a connection with no cached schema the first query pays one live extraction,
the same cost the first `describe_schema` pays today.

`ConnectionTester` becomes the boundary that keeps driver text out of the browser:

```csharp
public enum ConnectionFailure
{ None, AuthenticationFailed, DatabaseNotFound, HostUnreachable, Timeout, Unknown }

// provider level — Diagnostic is for the log and nothing else
public record ConnectionTestResult(
    bool Success, ConnectionFailure Failure, string? Diagnostic,
    string? ServerVersion = null, long ElapsedMs = 0);

// what any caller above Storage sees — Diagnostic does not reach it
public record ConnectionTestOutcome(
    bool Success, ConnectionFailure Failure, string? ServerVersion, long ElapsedMs);
```

`ConnectionTester` logs `Diagnostic` and returns `ConnectionTestOutcome`, so the
type system prevents the leak rather than a comment asking politely — which
matters here, since the comment currently in place turned out to describe
something the code does not do.

Classification is a pure function of what the provider extracted from its driver,
not a method taking the exception: `SqlState` on Npgsql (`28P01` and `28000` →
`AuthenticationFailed`, `3D000` → `DatabaseNotFound`), `Number` on SqlClient
(18456 → `AuthenticationFailed`, 4060 → `DatabaseNotFound`, 53 →
`HostUnreachable`, -2 → `Timeout`), plus socket-failure and timeout signals.
Pure, because `SqlException` cannot be constructed publicly and a classifier
taking exceptions could not be unit-tested at all. The thin `catch` in each
provider is covered by the opt-in integration tests.

The `ConnectionTestResult.Ok` factory keeps its signature, which is what matters:
it stands in a stub in nearly every test file that touches a provider, and none of
them care about failure. The `Fail` call sites all change, and there are four —
one in each provider, one in `ProviderTests`, and one in the `ConnectionsPageTests`
file this phase deletes outright.

### Components

New:

| Component | Job |
|---|---|
| `DatabaseSection.razor` | Sidebar section: label, add button, collapsible list of databases, each row carrying the engine, the name, and a status dot. |
| `DatabasePage.razor` | `/database/{id?}` — the two panels. |
| `ConnectionPanel.razor` | Name, DBMS, connection string, read-only toggle, Test, Save, Delete. |
| `ObjectsPanel.razor` | Schema groups, name filter, per-object level. |
| `ConnectionDeleteDialog.razor` | Confirmation before deleting a connection, following `ProjectDeleteDialog`. |

Removed, with their tests: `SchemaRail.razor` (`SchemaRailTests.cs`),
`Connections.razor` and the `/connections` route (`ConnectionsPageTests.cs`).
Those two test files are replaced by tests of the new surfaces, not ported.

Changed: `Sidebar.razor` hosts `DatabaseSection` first in `sidebar-body`, above
`ProjectSection`; `SidebarNav`'s `/connections` row becomes a "Databases" row
pointing at `/database`, keeping its `database` glyph; `Workspace.razor` takes
over the connection picker; `AttachmentMenu`'s empty state links to `/database`;
`SearchDialog` sends a database hit to `/database/{id}`.

`Sidebar.razor`'s comment block above the `<aside>` explains why `@onkeydown` is
attached conditionally and uses `SchemaRail`'s filter input as its worked example.
After this phase the sidebar holds no text input at all. The comment is rewritten
rather than left reasoning about a component that no longer exists — a stale
justification is how a load-bearing condition gets removed as dead weight.

### The config page

`/database` with no id is a new connection; `/database/{id}` is a saved one.

**Connection panel.** The secret rules carry over verbatim: the connection-string
field starts blank when editing, and blank on save keeps the stored secret. Test
reports server version and elapsed time on success, and one of the classified
outcomes on failure. Delete goes behind `ConnectionDeleteDialog` — today
`Connections.razor` deletes a connection with no confirmation at all, which puts
it alone among this application's destructive operations.

**Objects panel.** Appears after a successful test. Opening `/database/{id}` for a
saved connection runs that test once on mount, so the panel is present on arrival;
a new connection has nothing to test until the user presses the button. Inside:
groups by schema, a name filter, and a `Segmented` control per object carrying the
three levels — two for a view. A schema header row sets every object beneath it.

Schema groups are collapsed by default when there is more than one schema. The
filter binds on `@oninput`, which is a round trip per keystroke — the same one B2
accepted deliberately for search, and here it only filters a list already in
memory. Each level change writes immediately and invalidates `SchemaCache`, as
the rail's toggle does today; the panel has no Save button.

**Status dot.** Three states — untested, ok, failed — held per connection id in
`AppState`, so it lives for the circuit and no longer. The config page records a
test result and raises an event the section listens to, following the
`ConnectionsChanged` pattern that exists because a sidebar section is a sibling of
the page, not a child.

## Error handling

Unchanged in shape from every other surface: a deliberate refusal renders through
`OutcomeMessage` with a stable code, and a failure renders a fixed sentence with
no code and sends the detail to the server log.

New codes: `policy_denied_view_write`, `policy_denied_readonly_object`,
`schema_unavailable`, `connection_test_auth_failed`,
`connection_test_database_not_found`, `connection_test_host_unreachable`,
`connection_test_timeout`. The existing `connection_test_failed` becomes the
`Unknown` case.

**Accepted limitation: the view-write guard is only as fresh as the cache.**
`SchemaCache` has no expiry, and nothing invalidates it when the database's own
structure changes — the only invalidators are policy edits. So a view created
after the cache warmed is absent from `ViewList`, resolves to full access with
`IsView` false, and a write to it executes. That is the failure this phase exists
to close, deferred rather than prevented.

It is accepted rather than fixed because the staleness is not new: a table
created after the cache warmed is equally invisible to the model, and has been
since `SchemaCache` was introduced. What this phase adds is a security decision
resting on it. Both available remedies — an age bound on `GeneratedAt`, or an
explicit refresh action — are behaviour this spec never designed, and the age
bound changes the cache's contract for every consumer. Bounding the window
belongs with whichever phase gives the config page a refresh control; until then
it is recorded here and carried on the manual checklist rather than left
invisible.

`schema_unavailable` is the fail-closed answer when the schema cannot be read at
execution time. Identifying a view requires it, so a connection that can run a
query but cannot read its own catalog is refused where it previously executed.
That is a real behaviour change and it is accepted deliberately: such a
connection is already unusable for `describe_schema` and the entire
natural-language path, and the alternative is a write to a view passing
unnoticed — the failure this phase exists to close. As everywhere else, the
provider's own text goes to the log and a fixed sentence goes to the caller.

An unreachable database while the Objects panel is loading is an ordinary event
for a tool that points at arbitrary servers: the panel says its list could not be
read, and the page keeps working.

## Testing

Unit:

- `SchemaModel.Build` and `Filter` with views, including a hidden view absent from
  the filtered schema.
- `SqlAnalyzer` write-target extraction on both dialects: `INSERT`,
  `INSERT ... SELECT` from a second object, `UPDATE ... FROM`, `DELETE` with an
  alias, and a write target wrapped in a CTE. `Tables` must still list everything.
- `SqlPolicyValidator`: the denial order above, and unqualified-name resolution
  taking the most restrictive level and the view flag from any match.
- The connection-failure classifier, over the `SqlState` and error-number tables.

Store-backed:

- `TablePolicyService` across both dimensions, including the view/`Full` refusal,
  the schema-header batch, and `SchemaCache` invalidation on every write path.
- `QueryExecutionService` denying a write to a Read-only object and a write to a
  view, allowing a write to a Full-access object on a writable connection,
  refusing with `schema_unavailable` when the schema cannot be read, and reading
  the schema once rather than per query.
- Migration: a store carrying `TablePolicy` rows and a populated `SchemaCache`
  migrates without error, the policy rows survive untouched, and the cache comes
  out empty — followed by a read that repopulates it with views present.

bUnit:

- `DatabaseSection` renders a row per connection with engine, name, and dot state.
- The Objects panel is absent before a successful test and present after one.
- A level change calls the service; a schema header sets every object beneath it;
  a view offers two levels and not three.

Provider integration (opt-in, existing fixtures): view extraction on both engines.

Manual checklist, each row with a sentence saying why no automated test replaces
it — the lesson B2 recorded, that focus and browser behaviour are not reviewable
by reading:

- Focus returns to the field when `NameDialog` is re-shown with a "name already
  taken" error.
- The status dot changes on a test and holds for the session.
- The delete confirmation appears, cancels cleanly, and returns focus.
- The Objects panel on a database with many schemas is usable.

## Debts this phase closes

| Debt | Fix |
|---|---|
| `launch-url.txt` and `sqlagent.db` are not in `.gitignore`; the first holds a live launch token | Both, with the SQLite journal files. |
| The store is not copied before pending migrations are applied — recommended by B1's final review and again by B2's | `StoreInitializer` writes a `.bak` beside the store when `GetPendingMigrationsAsync()` returns anything. C1 adds a migration, so this is the phase to do it in. |
| Re-showing `NameDialog` with an error does not return focus to the field, because the component is reused and `firstRender` is false | Focus on re-show, driven by the error being set rather than by first render. |

Three more are closed as a side effect of the work rather than as separate items:
`Connections.razor` losing its unstyled markup, connection deletion gaining a
confirmation, and `Sidebar.razor`'s comment ceasing to describe a deleted
component.

## Definition of done

- Views appear in the Objects panel and in the natural-language prompt, marked
  as views so the model does not generate a write it can only be refused for; a
  hidden view is absent from both. The MCP `describe_schema` response still
  carries tables only, and extending it is C2's — its response shape is a
  published contract that hosts parse, so widening it is a versioning decision
  in its own right rather than a side effect of adding view extraction.
- Each level produces the documented `TablePolicy` state, and a schema header row
  sets every object beneath it.
- A write to a Read-only object is denied `policy_denied_readonly_object`; a write
  to a view is denied `policy_denied_view_write`; a read from a Read-only object
  inside a write statement is allowed.
- An object with no policy row is writable on a writable connection.
- Changing any level invalidates `SchemaCache`.
- A failed connection test renders a classified outcome and no driver text; the
  driver text is in the server log.
- `/connections` and the schema rail are gone, `/sql` picks its own connection,
  and every former link to `/connections` reaches `/database`.
- A store created before this phase migrates with its policy rows intact and its
  schema cache cleared, a `.bak` sits beside it, and the next schema read carries
  views.
- The full suite passes, and the manual checklist has been walked.
