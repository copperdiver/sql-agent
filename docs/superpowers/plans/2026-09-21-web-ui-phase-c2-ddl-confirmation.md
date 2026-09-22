# Web UI Phase C2 — DDL Permissions and Confirmation Implementation Plan

> For agentic workers: use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Classify supported DDL, persist per-connection DDL permissions, and enforce permission plus confirmation checks at the shared SQL execution boundary.

**Architecture:** Keep statement parsing and operation classification in SqlAgent.Core; keep the persisted permission flag and connection configuration in SqlAgent.Storage; make QueryExecutionService the single enforcement point used by SQL, NL, and MCP callers. The database page gets a third Structure permissions panel, while the chat confirmation UI remains Phase D as specified.

**Tech Stack:** .NET 10, C# records/enums, SqlParserCS 0.6.5, EF Core SQLite migrations, Blazor Server/bUnit, xUnit.

**Spec:** docs/superpowers/specs/2026-08-12-web-ui-overhaul-design.md, Phase C2 sections.

## Global Constraints

- AllowedDdl.None is the default for existing and new connections.
- Only CreateTable, AlterTable, DropTable, CreateIndex, DropIndex, and Truncate are configurable.
- GRANT, EXEC, CREATE VIEW, routines, and every unsupported parser shape remain denied unconditionally.
- Permission denial is policy_denied_ddl; permitted but unconfirmed DDL/model-generated writes are ddl_confirmation_required.
- Hand-written SQL from /sql passes confirmed: true; NL and MCP callers do not.
- Provider exception text never reaches browser responses or audit deny reasons.
- Every production change is preceded by a failing test and followed by focused plus full-suite verification.

---

### Task 1: Core DDL operation model and parser classification

Files:
- Modify: src/SqlAgent.Core/Policy/SqlPolicy.cs
- Test: tests/SqlAgent.Tests/SqlPolicyValidatorTests.cs
- Test: tests/SqlAgent.Tests/WriteTargetTests.cs

Interfaces:
- Add DdlOperation with Unsupported, CreateTable, AlterTable, DropTable, CreateIndex, DropIndex, Truncate.
- Add [Flags] AllowedDdl with None, CreateTable, AlterTable, DropTable, CreateIndex, DropIndex, Truncate.
- Add SqlStatementKind.Ddl.
- Add a trailing nullable DdlOperation property to ParsedStatement.

Steps:
- [x] Write failing tests that classify CREATE TABLE, ALTER TABLE, DROP TABLE, CREATE INDEX, DROP INDEX, and TRUNCATE for Postgres and SQL Server.
- [x] Add tests proving CREATE VIEW, GRANT, and EXECUTE remain Other/Unsupported.
- [x] Run the focused policy tests and observe the expected compile failure or old classification.
- [x] Implement classification using SqlParserCS AST nodes: Statement.CreateTable, Statement.CreateIndex, Statement.Truncate, and Statement.Drop with ObjectType.Table/Index. Leave unsupported nodes fail-closed.
- [x] Run the focused tests and verify they pass.
- [x] Commit with message: Add DDL operation classification.

### Task 2: Core policy decisions for permissions and confirmation

Files:
- Modify: src/SqlAgent.Core/Policy/SqlPolicy.cs
- Test: tests/SqlAgent.Tests/SqlPolicyValidatorTests.cs

Interfaces:
- Extend SqlPolicyValidator.Validate with AllowedDdl allowedDdl = AllowedDdl.None and bool confirmed = true while preserving existing call sites.
- Carry DdlOperation? in PolicyDecision.

Steps:
- [x] Write failing tests for: no permission gives policy_denied_ddl; permitted but unconfirmed DDL gives ddl_confirmation_required; permitted and confirmed DDL is allowed; unsupported GRANT/EXEC remains denied even with all flags; unconfirmed DML gives ddl_confirmation_required; read-only connection denial still wins for DML.
- [x] Run focused tests and confirm they fail because DDL is still policy_denied_unsupported and writes do not require confirmation.
- [x] Implement policy ordering: parse/empty/multi-statement checks; unsupported statements; connection read-only; mapped DDL permission; object visibility/view/read-only-object checks; confirmation for DDL and writes; allow.
- [x] Keep normalized SQL and operation in every denial decision.
- [x] Run focused tests, then the complete test project.
- [x] Commit with message: Enforce DDL permission and confirmation policy.

### Task 3: Persist AllowedDdl and expose connection configuration

Files:
- Modify: src/SqlAgent.Storage/Entities.cs
- Modify: src/SqlAgent.Storage/SqlAgentDbContext.cs
- Modify: src/SqlAgent.Storage/DatabaseConnectionService.cs
- Create: src/SqlAgent.Storage/Migrations/20260921_AllowedDdl.cs
- Create: src/SqlAgent.Storage/Migrations/20260921_AllowedDdl.Designer.cs
- Modify: src/SqlAgent.Storage/Migrations/SqlAgentDbContextModelSnapshot.cs
- Test: tests/SqlAgent.Tests/DatabaseConnectionServiceTests.cs
- Test: tests/SqlAgent.Tests/StoreMigrationTests.cs

Interfaces:
- Add DatabaseConnection.AllowedDdl, stored as a non-null integer with default 0.
- Expose AllowedDdl in DatabaseConnectionInfo.
- Add SetAllowedDdlAsync(Guid id, AllowedDdl allowedDdl, CancellationToken ct = default).
- CreateAsync and UpdateAsync preserve permissions and do not reset them.

Steps:
- [x] Write failing tests for default None, flags round-trip, update, missing connection, and migration from an old EnsureCreated store.
- [x] Run the focused tests and observe the expected missing API/default failure.
- [x] Add the field, EF mapping, service method, and migration. Existing stores must receive 0 without touching secrets, chats, policies, or caches.
- [x] Run focused migration tests and inspect the generated migration.
- [x] Commit with message: Persist per-connection DDL permissions.

### Task 4: Enforce the shared execution boundary and update callers

Files:
- Modify: src/SqlAgent.Core/QueryExecution.cs
- Modify: src/SqlAgent.Storage/QueryExecutionService.cs
- Modify: src/SqlAgent.Api.Mcp/McpToolService.cs
- Modify: src/SqlAgent.Host/Components/Pages/Workspace.razor
- Test: tests/SqlAgent.Tests/QueryExecutionServiceTests.cs
- Test: tests/SqlAgent.Tests/McpToolServiceTests.cs

Interfaces:
- Add confirmed = false to ExecuteSqlAsync(Guid connectionId, string sql, bool confirmed = false, CancellationToken ct = default).
- Add DdlOperation? Operation to QueryExecutionResult failures.
- /sql passes confirmed: true because the user typed and explicitly pressed Run.
- MCP keeps the default false, so headless writes and DDL cannot execute.

Steps:
- [x] Write failing service tests for permitted DROP without confirmation, the same DROP with confirmation, denied DDL even when confirmed, unconfirmed UPDATE, confirmed UPDATE, audit stability, and MCP refusal.
- [x] Run focused tests and observe the expected compile failure or old execution behavior.
- [x] Load info.AllowedDdl, call the expanded validator, map PolicyDecision to QueryExecutionResult, and keep all denials audited before provider execution.
- [x] Update Workspace and direct write tests to pass confirmed: true. Leave NL and MCP on false.
- [x] Run focused tests and the complete solution suite.
- [x] Commit with message: Require confirmation for writes and permitted DDL.

### Task 5: NL confirmation outcome without executing generated writes

Files:
- Modify: src/SqlAgent.Storage/NlQueryService.cs
- Modify: src/SqlAgent.Storage/ChatTurnService.cs
- Modify: src/SqlAgent.Storage/ChatEntities.cs
- Test: tests/SqlAgent.Tests/NlQueryServiceTests.cs
- Test: tests/SqlAgent.Tests/ChatTurnServiceTests.cs

Interfaces:
- Add NlResponseKind.ConfirmationRequired.
- Add NlQueryResult.Confirmation(string generatedSql, string operation, DdlOperation? ddlOperation).
- NL always calls execution with confirmed: false and maps only ddl_confirmation_required to ConfirmationRequired, preserving SQL and operation.
- Until Phase D adds the persisted/rendered confirmation outcome, ChatTurnService stores the safe stable code and generated SQL as an error-shaped assistant message; no mutation occurs.

Steps:
- [x] Write failing tests for generated UPDATE and permitted DROP returning ConfirmationRequired, preserving SQL, and never calling the provider. Keep SELECT, clarification, llm_not_configured, and ordinary errors unchanged.
- [x] Run focused tests and observe the expected red state.
- [x] Implement the mapping, falling back to operation write for DML.
- [x] Run focused and full tests.
- [x] Commit with message: Return confirmation-required outcomes for model writes.

### Task 6: Structure permissions panel on the database page

Files:
- Create: src/SqlAgent.Host/Components/Shared/Database/StructurePermissions.razor
- Create: src/SqlAgent.Host/Components/Shared/Database/StructurePermissions.razor.css
- Modify: src/SqlAgent.Host/Components/Pages/DatabasePage.razor
- Test: tests/SqlAgent.Tests/DatabasePageTests.cs
- Test: tests/SqlAgent.Tests/DesignSystemTests.cs

Interfaces:
- The panel receives ConnectionId, loads DatabaseConnectionInfo.AllowedDdl, and saves checkbox changes through SetAllowedDdlAsync.
- The master toggle selects/clears all six supported operations.
- The panel explains that views, routines, EXEC, GRANT, and unsupported operations are always denied.

Steps:
- [x] Write failing component tests for the third panel, six unchecked defaults, persistence of one operation, restoring saved flags, and master select/clear.
- [x] Run focused component tests and observe the missing component/API failure.
- [x] Implement with ScopedRunner, stable data-testid attributes, safe error rendering, and tokenized CSS.
- [x] Mount it after ObjectsPanel and show it only after a successful connection test.
- [x] Run focused tests and the design-system checks.
- [x] Commit with message: Add database structure permissions panel.

### Task 7: Documentation, migration rehearsal, and final verification

Files:
- Modify: docs/web-ui.md
- Modify: docs/runbook.md
- Modify: README.md
- Modify: this plan

Steps:
- [x] Document the six toggles, default None, policy_denied_ddl versus ddl_confirmation_required, /sql confirmation, and NL/MCP limitations.
- [x] Rehearse migration against an old store and verify AllowedDdl is zero with no data loss.
- [ ] Run:
~~~powershell
dotnet restore SqlAgent.slnx
dotnet build SqlAgent.slnx --configuration Release --no-restore
dotnet test SqlAgent.slnx --configuration Release --no-build --logger "console;verbosity=minimal"
~~~
- [x] Record warnings and the AngleSharp advisory accurately.
- [ ] Commit with message: Document C2 DDL permissions and confirmation.

## Definition of Done

- Supported DDL is classified without regex and unsupported statements remain fail-closed.
- AllowedDdl persists through migration and is configurable from the database page.
- Permission and confirmation checks happen before provider execution.
- NL-generated DML/DDL returns ConfirmationRequired without executing.
- MCP cannot execute unconfirmed writes or DDL.
- Existing reads, visibility, view-write checks, cancellation, timeout, audit, and error redaction remain green.
- Build and all tests pass; known warnings are reported accurately.
