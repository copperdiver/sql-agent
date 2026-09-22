# Web UI Phase D — Chat Components Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the interim chat outcome rendering with safe SQL blocks, explicit confirmation, a reusable data table, a chat scratchpad, and policy-filtered schema diagrams.

**Architecture:** Keep SQL execution in `QueryExecutionService`, and make Chat confirmation call that service with `confirmed: true`; the UI never becomes a second policy engine. Store confirmation outcomes as first-class chat messages so a reload can render the pending action, while live rows remain memory-only. Build pure tokenizer/diagram helpers separately from Blazor components so safety and policy filtering are unit-testable without a browser.

**Tech Stack:** .NET 10, Blazor Server, bUnit, xUnit, EF Core SQLite migrations, Markdig 0.38.0, CodeMirror, Mermaid lazy import, existing `ResultExport` and `Modal`/`DialogService`.

**Spec:** `docs/superpowers/specs/2026-08-12-web-ui-overhaul-design.md`, Phase D sections.

## Global Constraints

- Confirmation must remain enforced at `QueryExecutionService`; UI buttons are not the security boundary.
- Reads run directly; DDL and writes show the operation/target warning and require explicit confirmation.
- Model-generated SQL is displayed as text or escaped highlighted markup; generated SQL and cell values must never become raw HTML.
- Hidden tables/views must not appear in schema diagrams; diagrams are generated from the same filtered schema as NL prompts.
- Chat rows persist metadata and pending SQL, never result rows.
- Existing `/sql` behavior, CSV/JSON export, cancellation, timeout, audit logging, and C2 policy codes must stay green.
- Mermaid is not part of the startup path; load it only when the first diagram is rendered.
- Each task starts with a failing test, ends with focused verification, and is committed separately.

---

### Task 1: Persist first-class confirmation outcomes

**Files:**
- Modify: `src/SqlAgent.Storage/ChatEntities.cs`
- Modify: `src/SqlAgent.Storage/ChatService.cs`
- Modify: `src/SqlAgent.Storage/ChatTurnService.cs`
- Modify: `src/SqlAgent.Storage/NlQueryService.cs` only if the result contract needs a small compatibility helper
- Create: EF migration for `ChatMessage.ConfirmationOperation`
- Test: `tests/SqlAgent.Tests/ChatTurnServiceTests.cs`
- Test: `tests/SqlAgent.Tests/ChatServiceTests.cs`
- Test: `tests/SqlAgent.Tests/StoreMigrationTests.cs`

**Interfaces:**
- Add `ChatOutcomeKind.ConfirmationRequired`.
- Add trailing nullable `ConfirmationOperation` to `ChatMessage`, `ChatMessageView`, and `ChatMessageInput` so existing positional call sites remain source-compatible.
- `ChatTurnService` stores `NlResponseKind.ConfirmationRequired` as `ChatOutcomeKind.ConfirmationRequired`, retaining generated SQL, stable code, and operation instead of the temporary C2 error-shaped result.
- Add `ChatTurnService.ConfirmAsync(Guid assistantMessageId, CancellationToken)` returning a `ChatConfirmationResult` containing the updated assistant message and optional live `NlQueryResult`.
- Confirmation resolves the single database attached to the preceding user message, executes the stored SQL through `QueryExecutionService` with `confirmed: true`, and updates the same assistant row; it never appends a second assistant answer.

**Steps:**
- [ ] Write tests for persisted `ConfirmationRequired`, operation/SQL round-trip, exactly-one-database resolution, confirmed execution updating the same message, and policy/execution failure persistence.
- [ ] Run the focused tests and observe missing enum/field/service failures.
- [ ] Add the enum, stored field, migration, and `ChatService` read/update seams.
- [ ] Implement `ConfirmAsync` with cancellation-safe stable outcomes and no raw provider exception text.
- [ ] Run chat, migration, and execution-focused tests.
- [ ] Commit: `Persist chat confirmation outcomes`.

### Task 2: Build safe SQL highlighting and reusable DataTable

**Files:**
- Create: `src/SqlAgent.Host/Web/SqlHighlighter.cs`
- Create: `src/SqlAgent.Host/Components/Shared/Chat/SqlBlock.razor`
- Create: `src/SqlAgent.Host/Components/Shared/Chat/SqlBlock.razor.css`
- Create: `src/SqlAgent.Host/Components/Shared/DataTable.razor`
- Create: `src/SqlAgent.Host/Components/Shared/DataTable.razor.css`
- Modify: `src/SqlAgent.Host/Components/Shared/ResultGrid.razor` to delegate to `DataTable` while preserving its current `QueryExecutionResult` API
- Test: `tests/SqlAgent.Tests/SqlHighlighterTests.cs`
- Test: `tests/SqlAgent.Tests/SqlBlockTests.cs`
- Test: `tests/SqlAgent.Tests/DataTableTests.cs`
- Update: `tests/SqlAgent.Tests/ResultGridTests.cs`

**Interfaces:**
- `SqlHighlighter.Highlight(string sql, DatabaseProviderType provider)` returns escaped markup generated only from tokenizer tokens; it must preserve SQL text while classifying keywords, strings, numbers, comments, and identifiers.
- `SqlBlock` accepts SQL, provider/dialect, optional `NlQueryResult`, `Restored`, `OnRun`, and `OnEdit`; it renders dialect badge, copy/edit/run actions, amber write/DDL warning, and a `DataTable` for live results.
- `DataTable` accepts columns, rows, row count/truncated metadata, and export callbacks/filename context; it renders NULL distinctly, pages 25/50/100, expands long values, and uses `ResultExport` plus `sqlAgentDownload`.

**Steps:**
- [ ] Add failing tokenizer tests for escaping `<script>`, quotes/comments, SQL keywords, and plain identifiers.
- [ ] Add failing bUnit tests for dialect label, warning copy, read-vs-write Run behavior, null cells, paging, long-value expansion, truncation, and CSV/JSON wiring.
- [ ] Implement pure tokenization/highlighting and shared table behavior with tokenized CSS only.
- [ ] Replace the interim result table in Chat and `/sql` through the compatibility seam without changing export contracts.
- [ ] Run focused component/helper tests and existing ResultGrid/Workspace tests.
- [ ] Commit: `Add SQL blocks and reusable data table`.

### Task 3: Wire Chat rendering and confirmation dialog

**Files:**
- Modify: `src/SqlAgent.Host/Components/Shared/Chat/AssistantMessage.razor`
- Modify: `src/SqlAgent.Host/Components/Shared/Chat/MessageList.razor`
- Modify: `src/SqlAgent.Host/Components/Shared/ChatOutcome.razor` or remove it after all callers move to the new components
- Create: `src/SqlAgent.Host/Components/Shared/Chat/ConfirmDialog.razor`
- Create: `src/SqlAgent.Host/Components/Shared/Chat/ConfirmDialog.razor.css`
- Modify: `src/SqlAgent.Host/Components/Pages/ChatPage.razor`
- Modify: `src/SqlAgent.Host/Components/Shared/Chat/AssistantMessage.razor.css`
- Test: `tests/SqlAgent.Tests/ChatOutcomeTests.cs`
- Test: `tests/SqlAgent.Tests/ChatPageTests.cs`
- Create/update: `tests/SqlAgent.Tests/ConfirmDialogTests.cs`

**Interfaces:**
- Restore `ChatOutcomeKind.ConfirmationRequired` into `NlQueryResult` with SQL and operation when a conversation is reloaded.
- `AssistantMessage` receives `OnConfirm` and `OnOpenInEditor`; it shows the warning block and keeps confirmation local to the message being acted on.
- `ConfirmDialog` receives SQL, operation/target label, `OnConfirm`, and `OnCancel`; it must identify the destructive action and default focus safely through `Modal`.
- `ChatPage` calls `ChatTurnService.ConfirmAsync`, replaces the matching message in `_messages`, and places the returned live result in `_live`; it must reject duplicate confirmations while busy and preserve the transcript on cancellation.

**Steps:**
- [ ] Add failing tests for restored pending confirmation, warning text, cancel-without-execution, confirm-through-service, success replacing the pending outcome, and safe failure rendering.
- [ ] Run focused Chat tests and capture the red state.
- [ ] Implement component mapping and page callback flow using `DialogService`/`Modal`.
- [ ] Ensure confirmation updates the same message, refreshes sidebar state only when needed, and never appends a duplicate answer.
- [ ] Run all Chat, dialog, and storage tests.
- [ ] Commit: `Render and confirm pending chat SQL`.

### Task 4: Add Chat scratchpad and composer actions

**Files:**
- Modify: `src/SqlAgent.Host/Components/Pages/ChatPage.razor`
- Create: `src/SqlAgent.Host/Components/Shared/Chat/ScratchPad.razor`
- Create: `src/SqlAgent.Host/Components/Shared/Chat/ScratchPad.razor.css`
- Modify: `src/SqlAgent.Host/Components/Shared/Chat/Composer.razor`
- Modify: `src/SqlAgent.Host/Components/Shared/Chat/SqlBlock.razor`
- Test: `tests/SqlAgent.Tests/ChatPageTests.cs`
- Test: `tests/SqlAgent.Tests/ComposerTests.cs`
- Test: `tests/SqlAgent.Tests/WorkspaceTests.cs`

**Interfaces:**
- Scratchpad reuses `SqlEditor` and `DataTable`, receives the selected connection, starts with SQL from a block, and calls `ExecuteSqlAsync(..., confirmed: true)` after an explicit Run.
- Composer Tools gains Open scratchpad; SqlBlock Edit opens the same scratchpad with its SQL; Close preserves no stale pending SQL.
- Read/DDL/write behavior remains consistent with `/sql`, including cancel, row caps, exports, and policy failures.

**Steps:**
- [ ] Add failing tests for opening from a block, round-tripping SQL, explicit confirmed execution, cancel, close/reopen state, and export.
- [ ] Run Chat/Workspace focused tests and observe missing scratchpad behavior.
- [ ] Implement the panel with the existing CodeMirror seam and shared DataTable.
- [ ] Run Chat, Workspace, SqlEditor, and ResultGrid tests.
- [ ] Commit: `Add chat scratchpad`.

### Task 5: Schema diagram source and lazy Mermaid rendering

**Files:**
- Create: `src/SqlAgent.Host/Web/MermaidSource.cs`
- Create: `src/SqlAgent.Host/Components/Shared/Chat/ErDiagram.razor`
- Create: `src/SqlAgent.Host/Components/Shared/Chat/ErDiagram.razor.css`
- Modify: `src/SqlAgent.Host/Components/Shared/Chat/Composer.razor`
- Modify: `src/SqlAgent.Host/Components/Shared/Chat/AttachmentMenu.razor` or Tools menu owner
- Modify: `src/SqlAgent.Storage/ChatEntities.cs` to add `ChatOutcomeKind.SchemaDiagram` and nullable `SchemaDiagramConnectionId` on the assistant message/view/input
- Modify: `src/SqlAgent.Storage/ChatService.cs` and `src/SqlAgent.Storage/ChatTurnService.cs` to persist and reload diagram metadata
- Create: EF migration for `SchemaDiagramConnectionId`
- Add: `src/SqlAgent.Host/wwwroot/lib/mermaid/mermaid.min.js` and its MIT license file under `wwwroot/lib/mermaid`
- Test: `tests/SqlAgent.Tests/MermaidSourceTests.cs`
- Test: `tests/SqlAgent.Tests/ChatPageTests.cs`

**Interfaces:**
- `MermaidSource.Build(DatabaseSchema schema)` returns escaped, deterministic `erDiagram` source from the already policy-filtered schema; hidden objects cannot be reintroduced.
- One attached database is required. The diagram action reads the filtered schema, stores a `SchemaDiagram` assistant outcome, and reloads by re-deriving from live schema rather than persisting stale diagram text.
- `ErDiagram` dynamically imports Mermaid on first render, passes CSS token colors, and exposes zoom/reset/fullscreen/download controls.

**Steps:**
- [ ] Add failing pure tests for PK/FK output, deterministic names, view inclusion, and omission of hidden objects.
- [ ] Add failing page/component tests for one-database requirement and Tools entry point.
- [ ] Implement source generation, outcome persistence, lazy import, theme token handoff, and safe diagram download.
- [ ] Add manual checklist items for Mermaid rendering, theme changes, fullscreen, and SVG download.
- [ ] Run focused tests and the existing design-system suite.
- [ ] Commit: `Add policy-filtered schema diagrams`.

### Task 6: Composer polish, regenerate, docs, and final verification

**Files:**
- Modify: `src/SqlAgent.Host/Components/Pages/ChatPage.razor`
- Modify: `src/SqlAgent.Host/Components/Shared/Chat/MessageList.razor`
- Modify: `src/SqlAgent.Host/Components/Shared/Chat/UserMessage.razor`
- Modify: `src/SqlAgent.Host/Components/Shared/Chat/AssistantMessage.razor`
- Modify: `src/SqlAgent.Host/Components/Shared/Chat/Composer.razor`
- Modify: `docs/web-ui.md`
- Modify: `docs/runbook.md`
- Modify: `README.md`
- Test: `tests/SqlAgent.Tests/ChatPageTests.cs`, `ComposerTests.cs`, `RestyleRegressionTests.cs`

**Interfaces:**
- Add `Markdig` package reference version `0.38.0` to `src/SqlAgent.Host/SqlAgent.Host.csproj`; configure the pipeline with raw HTML disabled and render only sanitized server-produced markup.
- User messages get edit/copy actions; assistant messages get agent label, copy/regenerate/menu actions, and safe Markdown text rendering through that fixed Markdig dependency.
- Regenerate resends the preceding user prompt and replaces the assistant outcome in place; it never creates a duplicate visible answer.
- Model selector remains an explicit “No model configured” state until a provider exists.

**Steps:**
- [ ] Add failing tests for copy/edit, regenerate replacement, empty model state, keyboard behavior, and responsive action-row rendering.
- [ ] Implement only the interactions supported by existing services; keep unsupported model selection visibly disabled.
- [ ] Update manual checklist and documentation for SQL blocks, confirmation, scratchpad, DataTable, and diagrams.
- [ ] Run `dotnet restore SqlAgent.slnx`.
- [ ] Run `dotnet build SqlAgent.slnx --configuration Release --no-restore`.
- [ ] Run `dotnet test SqlAgent.slnx --configuration Release --no-build --logger "console;verbosity=minimal"`.
- [ ] Record warnings and known advisories accurately.
- [ ] Commit: `Document Phase D chat components`.

## Definition of Done

- A read SQL block runs once and displays a reusable paged DataTable with safe NULL/long-value behavior and exports.
- A DDL/write block cannot execute without the C2 service confirmation gate; the confirmation dialog is not the security boundary.
- Pending confirmation survives reload and confirmation updates the existing assistant message.
- Scratchpad round-trips SQL and executes typed SQL with `confirmed: true`.
- Mermaid source is pure, deterministic, and generated from policy-filtered schema; the browser library loads lazily.
- Existing C2, C1, persistence, cancellation, audit, export, and error-redaction tests remain green.
