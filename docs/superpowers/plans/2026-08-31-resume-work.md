# Work Resumption Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Resume development from the merged Phase C1 baseline, close its verification debt, and continue toward a useful LLM-backed chat experience.

**Architecture:** Preserve the current .NET 10 and Blazor Server architecture. First restore a warning-free release baseline and formally close Phase C1; then add the LLM integration as its own designed subsystem before building the Phase D and E user-facing capabilities.

**Tech Stack:** .NET 10, ASP.NET Core, Blazor Server, EF Core with SQLite, xUnit, bUnit, PostgreSQL, SQL Server.

**Specs:** `docs/superpowers/specs/2026-08-12-web-ui-overhaul-design.md`, `docs/superpowers/specs/2026-08-14-web-ui-phase-c1-databases-and-objects-design.md`

## Global Constraints

- Keep every project on `net10.0`.
- Keep the web listener bound to `127.0.0.1` unless a later remote-access spec explicitly changes the security model.
- Preserve the existing SQL policy boundaries and stable error codes.
- Do not expose driver exception text, connection strings, launch tokens, or API keys in the UI or logs.
- Every implementation task ends with a warning-free Release build and a green relevant test suite.

---

## 1. Restore a clean baseline

- [x] Resolve the known vulnerability warning caused by `AngleSharp 1.2.0` without suppressing `NU1902`.
- [x] Fix the three xUnit analyzer findings in `ChatPageTests`, `RestyleRegressionTests`, and `SqlEditorTests`.
- [x] Run `dotnet build SqlAgent.slnx --configuration Release --no-restore -warnaserror`.
- [x] Run `dotnet test SqlAgent.slnx --configuration Release --no-build`.
- [x] Record the verified test count and ensure the working diff contains only intentional changes.

Verification on 2026-08-31: Release build completed with 0 warnings and 0 errors; 616 tests passed with 0 failed and 0 skipped; `dotnet list package --include-transitive --vulnerable` reported no vulnerable packages.

## 2. Formally close Phase C1

- [ ] Walk the browser-only checklist in `docs/web-ui.md`.
- [ ] Exercise successful and failed connections against real PostgreSQL and SQL Server instances.
- [ ] Verify tables, views, and all three object-access levels end to end.
- [ ] Rehearse migration of a pre-C1 SQLite store and verify that its `.bak` is created.
- [ ] Record manual results and update the Phase C1 status and accepted limitations.

### C1 manual verification checklist (2026-08-31)

**1. Basic UI and security**

- [x] 1.1 — Chat, SQL, Databases, and Settings open without a full page reload.
- [x] 1.2 — A newly created database appears in the sidebar without reloading the page.
- [x] 1.3 — Opening the UI without a token or session cookie in a private window returns 401 Unauthorized.

**2. Connections**

- [x] 2.1 — Testing `qa-postgres` succeeds and reports the PostgreSQL version and elapsed time. Verified: PostgreSQL 16.14, 9 ms.
- [x] 2.2 — A successful test changes the database status dot to green without navigation. Fixed `.database-dot` rendering with `display: inline-block`; verified visually in the browser after a live SQL Server test.
- [x] 2.3 — A wrong PostgreSQL password produces a classified credentials error without driver text or a connection string. Verified against `qa-postgres`: `connection_test_auth_failed` with the safe credentials-rejected message.
- [x] 2.4 — A wrong PostgreSQL database name produces a classified database-not-found error without driver text. Verified against `qa-postgres`: `connection_test_database_not_found` with the safe database-not-found message.
- [x] 2.5 — A wrong PostgreSQL port produces a classified unreachable-host error without driver text. The SQL Server equivalent was reproduced against port `59999`; transport error `SqlException.Number=258` now maps to `connection_test_host_unreachable`.
- [x] 2.6 — A failed test changes the status dot to red. Fixed by the same `.database-dot` rendering change as 2.2; the state and accessible label were already updating correctly.
- [ ] 2.7 — Reloading the page resets status dots to neutral. **NOT YET VERIFIED:** the rendering defect from 2.2/2.6 is fixed; a fresh-page neutral-state check remains.
- [x] 2.8 — Reopening a database leaves the connection-string field blank.
- [x] 2.9 — Saving with a blank connection string preserves the existing secret and the next Test still succeeds.
- [ ] 2.10 — Delete opens a confirmation; Cancel keeps the database and returns focus to Delete. **PARTIAL:** confirmation, Cancel, and database preservation work; focus restoration is now implemented but still needs a final browser keyboard assertion.
- [x] 2.11 — The wrong-password, wrong-database, and wrong-port classifications also work against SQL Server. Wrong password and database were verified live; the exact wrong-port scenario (`127.0.0.1:59999`) now passes the provider integration test with `HostUnreachable`.
- [ ] 2.12 — For every classified failure, the browser hides driver text while the server log retains diagnostic details. **PARTIAL:** live SQL Server UI hides driver text for the classified password/database failures; server-log retention is covered by `ConnectionFailureTests` (23/23 passed), but direct live-console log capture was not available for the hidden host process.

**3. Objects panel**

- [x] 3.1 — Tables and views appear grouped by schema after a successful connection test. Verified in the browser on PostgreSQL `public` and SQL Server `dbo`.
- [x] 3.2 — Views are labelled and offer only Not visible and Read-only, never Full access. Verified on SQL Server `dbo.spt_values`.
- [x] 3.3 — Filtering by object name updates the visible list correctly. Verified with the `spt` filter on SQL Server.
- [x] 3.4 — Tables offer Not visible, Read-only, and Full access. Verified on both database pages.
- [x] 3.5 — A schema-header change updates every object in the schema and clamps views to Read-only. Verified by setting SQL Server `dbo` to Read-only; table controls moved to Read-only and the view exposed no Full access.
- [ ] 3.6 — Every segmented control is keyboard-accessible and exposes its selected state without relying on colour alone. **NOT FULLY VERIFIED:** controls are rendered as toggle buttons with `aria-pressed`, but a complete keyboard traversal was not performed.
- [ ] 3.7 — A database with several hundred objects remains responsive while filtering and expanding its largest schema.

**4. SQL policy**

- [x] 4.1 — Selecting from a Not visible table returns `policy_denied_hidden_table`. Verified in the browser with `SELECT * FROM public.orders`; UI showed the safe message and code.
- [ ] 4.2 — Updating a Read-only table returns `policy_denied_readonly_object`. **AUTOMATED VERIFIED:** QueryExecutionService/SqlPolicyValidator coverage passes; browser scenario remains.
- [ ] 4.3 — Writing to a view returns `policy_denied_view_write`. **AUTOMATED VERIFIED:** QueryExecutionService/SqlPolicyValidator coverage passes; browser scenario remains.
- [ ] 4.4 — A Full-access table on a writable connection accepts a write. **AUTOMATED VERIFIED:** QueryExecutionService coverage passes; browser scenario remains.
- [ ] 4.5 — A write on a read-only connection returns `policy_denied_readonly`. **AUTOMATED VERIFIED:** QueryExecutionService/SqlPolicyValidator coverage passes; browser scenario remains.
- [ ] 4.6 — An access-level change affects the next query without restarting the host. **AUTOMATED VERIFIED:** policy/service tests pass; browser scenario remains.

**5. Navigation and focus**

- [ ] 5.1 — Navigating to the same database again does not discard an unsaved name edit. **AUTOMATED COVERAGE PASSES:** DatabasePage tests cover in-place navigation; browser assertion remains.
- [ ] 5.2 — Re-navigation or a parent re-render does not make an already-open Objects panel disappear. **AUTOMATED COVERAGE PASSES:** page/objects tests pass; browser assertion remains.
- [ ] 5.3 — A duplicate project-name error returns focus to the input, and Escape closes the re-shown dialog. **AUTOMATED COVERAGE PASSES:** ProjectSection/NameDialog tests pass; browser assertion remains.

**6. Migration compatibility**

- [ ] 6.1 — Starting against a pre-C1 SQLite store preserves saved databases and creates a `.bak` before migration. **AUTOMATED VERIFIED:** StoreMigrationTests cover pre-migration stores and backup creation; standalone manual startup remains.
- [ ] 6.2 — The first schema read after migration includes views. **AUTOMATED COVERAGE PASSES:** migration/schema tests pass; standalone manual startup remains.
- [ ] 6.3 — A legacy policy row created by hiding and later unhiding an object is shown as Read-only and can be restored to Full access explicitly. **AUTOMATED COVERAGE PASSES:** policy/migration tests pass; standalone manual startup remains.

## 3. Create a release checkpoint

- [x] Review SQL write-target detection, schema caching, migrations, launch-token handling, and loopback HTTP protection. Targeted review suite passed: 123/123.
- [x] Build the Windows and systemd deployment artifacts. `win-x64` and `linux-x64` Release publishes completed; packaging script and systemd unit validated.
- [ ] Create a release commit or tag for the completed Phase C1 baseline.

Checkpoint note (2026-09-01): the release commit/tag remains pending because the working tree contains intentional uncommitted migration/test/UI changes and the manual browser-only C1 scenarios are not all closed.

## 4. Integrate a real LLM provider

- [ ] Write and approve a dedicated design covering provider/model configuration, secret storage, timeouts, cancellation, errors, schema delivery, audit, and limits.
- [ ] Add a deterministic fake provider for automated tests.
- [ ] Implement the provider behind `ILlmSqlGateway` without weakening the existing SQL-policy execution path.
- [ ] Verify that a natural-language question reaches a safe SQL result end to end.

## 5. Implement Phase D

- [ ] Add SQL blocks, result tables, ER diagrams, and the scratchpad to chat messages.
- [ ] Add Copy, Open, Run, and dangerous-operation confirmation flows.
- [ ] Cover the .NET boundaries automatically and walk the CodeMirror, Mermaid, and download checks in a browser.

## 6. Implement Phase E

- [ ] Add the file-storage abstraction and local-disk provider.
- [ ] Add upload, deletion, message attachment snapshots, and model handoff.
- [ ] Enforce file size/type limits and safe download headers.

## 7. Complete infrastructure phases

- [ ] Replace the Windows-only secret store with a portable implementation.
- [ ] Design and implement remote TLS access, real sessions, and multi-user operation.
- [ ] Add operational monitoring, structured audit review, and a repeatable release process.
