# Web UI Phase E — File Attachments Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add secure local file attachments to Chat, persist attachment metadata with sent messages, serve files only as hardened downloads, and leave a provider boundary for future remote storage.

**Architecture:** Keep bytes behind `IFileStorageProvider` and keep metadata in SQLite as `MessageAttachment`. A selected file is uploaded immediately into provider storage and held as pending circuit state; metadata is written atomically with the sent chat message, while unsent blobs are deleted on circuit teardown or by the age-gated startup sweep. The authenticated `/files/{id}` endpoint resolves only persisted metadata and always returns a download response, never inline content.

**Tech Stack:** .NET 10, Blazor Server, EF Core SQLite migrations, `IBrowserFile`, ASP.NET endpoint routing, existing `ScopedRunner`, bUnit, xUnit.

**Spec:** `docs/superpowers/specs/2026-09-22-web-ui-phase-e-file-attachments-design.md`

## Global Constraints

- The default provider key is `local-disk`; provider selection goes through `IFileStorageProviderRegistry`.
- `SqlAgent:Files:MaxBytes` defaults to 25 MiB (`25 * 1024 * 1024`) and the message cap is 10 files.
- Client filenames are display-only; no client filename may determine a directory or blob identity.
- File bytes never enter SQLite, `LlmSqlRequest`, SQL prompts, logs, or HTML markup.
- Upload, Send, download, deletion, and cleanup must return stable user-safe errors; provider exception text stays in logs.
- `GET /files/{id}` is behind the existing origin/token middleware and always uses `Content-Disposition: attachment`.
- Active content types (`text/html`, `image/svg+xml`, XML, and equivalent browser-active types) download as `application/octet-stream`.
- Existing database attachments, chat persistence, cancellation, projects, exports, and the 664-test baseline must remain green.
- Every task begins with a focused failing test, ends with focused verification, and is committed separately.

---

### Task 1: Define provider-neutral file contracts and limits

**Files:**
- Create: `src/SqlAgent.Core/Files/FileStorageContracts.cs`
- Modify: `src/SqlAgent.Core/Llm/LlmGateway.cs`
- Test: `tests/SqlAgent.Tests/FileStorageContractsTests.cs`
- Test: `tests/SqlAgent.Tests/NlQueryServiceTests.cs`

**Interfaces:**

```csharp
public interface IFileStorageProvider
{
    string Key { get; }
    Task<StoredFile> SaveAsync(FileUpload upload, CancellationToken ct = default);
    Task<Stream?> OpenReadAsync(string storageKey, CancellationToken ct = default);
    Task<bool> DeleteAsync(string storageKey, CancellationToken ct = default);
}

public interface IFileStorageProviderRegistry
{
    IFileStorageProvider Get(string key);
}

public record FileUpload(string FileName, string ContentType, Stream Content);
public record StoredFile(string StorageKey, string Url, long SizeBytes);
public record LlmFileAttachment(string FileName, string ContentType, string Url);
public record FileStorageOptions(string Provider = "local-disk", long MaxBytes = 25 * 1024 * 1024,
    int MaxAttachmentsPerMessage = 10);
```

- [ ] **Step 1: Write failing contract tests.** Assert a registry resolves by key, unknown keys fail with a stable exception, the default options are 25 MiB/10, and `LlmSqlRequest` can carry a trailing empty attachment list without changing existing positional call sites.
- [ ] **Step 2: Run the focused tests and verify the missing contracts fail compilation.**
- [ ] **Step 3: Add the contracts, options, registry, and a trailing defaulted `Attachments` property to `LlmSqlRequest`.** Keep the existing three constructor arguments source-compatible.
- [ ] **Step 4: Run `dotnet test tests/SqlAgent.Tests/SqlAgent.Tests.csproj --filter "FullyQualifiedName~FileStorageContractsTests|FullyQualifiedName~NlQueryServiceTests"`.**
- [ ] **Step 5: Commit `Define file storage contracts`.**

### Task 2: Implement secure local-disk storage

**Files:**
- Create: `src/SqlAgent.Storage/LocalDiskFileStorageProvider.cs`
- Create: `src/SqlAgent.Storage/FileStorageProviderRegistry.cs`
- Create: `src/SqlAgent.Storage/FileStorageService.cs`
- Modify: `src/SqlAgent.Storage/SqlAgent.Storage.csproj` only if a required hosting/options package is absent
- Test: `tests/SqlAgent.Tests/LocalDiskFileStorageProviderTests.cs`
- Test: `tests/SqlAgent.Tests/FileStorageServiceTests.cs`

**Interfaces:**

```csharp
public record PendingFileAttachment(
    string FileName, string ContentType, long SizeBytes,
    string ProviderKey, string StorageKey, string Url);

public Task<PendingFileAttachment> UploadAsync(
    FileUpload upload, CancellationToken ct = default);
public Task<bool> DeletePendingAsync(
    PendingFileAttachment pending, CancellationToken ct = default);
public Task SweepOrphansAsync(CancellationToken ct = default);
```

- [ ] **Step 1: Write failing provider tests.** Cover GUID-based paths under `<store>/files/yyyy/MM`, preserved safe extension only, no path traversal, exact size limit, async stream copy, `OpenReadAsync` missing-key null, and delete idempotence.
- [ ] **Step 2: Write failing service tests.** Cover rejection at 25 MiB, safe normalization of empty/overlong display names, provider registry selection, and orphan sweep deleting only files older than 24 hours with no matching metadata key.
- [ ] **Step 3: Run the focused tests and capture missing provider/service failures.**
- [ ] **Step 4: Implement `LocalDiskFileStorageProvider`.** Resolve the root from `HostInfo.StoreDirectory`/the configured store directory passed into DI, create year/month directories, write with `FileMode.CreateNew`, generate the GUID before opening, and never concatenate an unsanitized filename into the path.
- [ ] **Step 5: Implement the registry and `FileStorageService`.** Enforce max bytes while streaming, reject more than 10 only at message binding time, return `PendingFileAttachment`, log provider failures without returning provider text, and make orphan sweep skip files newer than 24 hours.
- [ ] **Step 6: Run provider/service tests and commit `Add secure local file storage`.**

### Task 3: Persist message attachments and migrate the store

**Files:**
- Modify: `src/SqlAgent.Storage/ChatEntities.cs`
- Modify: `src/SqlAgent.Storage/ChatService.cs`
- Modify: `src/SqlAgent.Storage/SqlAgentDbContext.cs`
- Create: `src/SqlAgent.Storage/MessageAttachmentService.cs`
- Generate: `src/SqlAgent.Storage/Migrations/*_FileAttachments.cs`
- Modify: `src/SqlAgent.Storage/Migrations/SqlAgentDbContextModelSnapshot.cs`
- Test: `tests/SqlAgent.Tests/ChatServiceTests.cs`
- Test: `tests/SqlAgent.Tests/StoreMigrationTests.cs`
- Test: `tests/SqlAgent.Tests/MessageAttachmentServiceTests.cs`

**Interfaces:**

```csharp
public record ChatFileRef(Guid Id, string FileName, string ContentType, long SizeBytes, string Url);

public class MessageAttachment
{
    public Guid Id { get; set; }
    public Guid ChatMessageId { get; set; }
    public ChatMessage? Message { get; set; }
    public string FileName { get; set; } = "";
    public string ContentType { get; set; } = "application/octet-stream";
    public long SizeBytes { get; set; }
    public string ProviderKey { get; set; } = "";
    public string StorageKey { get; set; } = "";
    public string Url { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}
```

`ChatMessageView` and `ChatMessageInput` gain trailing `IReadOnlyList<ChatFileRef>` parameters with empty defaults. `ChatService.AppendMessageAsync` writes rows with the message, `GetChatAsync` includes them, and the unique metadata boundary does not expose provider storage keys to the view.

- [ ] **Step 1: Add failing persistence tests.** Assert file metadata round-trips with a message, bytes/storage keys are absent from `ChatMessageView`, ten files succeed, eleven are rejected with `file_rejected`, and deleting a chat cascades attachment rows.
- [ ] **Step 2: Add a migration test against the previous model.** Start with the pre-Phase-E schema, apply migrations, and assert existing chats/messages survive and `MessageAttachments` exists.
- [ ] **Step 3: Run the focused persistence tests and observe missing entity/field/migration failures.**
- [ ] **Step 4: Add the entity, EF relationship/index, trailing DTO fields, attachment binding, and generated EF migration.** Use `dotnet ef migrations add FileAttachments --project src/SqlAgent.Storage` and keep the migration generated rather than hand-writing the model snapshot.
- [ ] **Step 5: Run `dotnet test tests/SqlAgent.Tests/SqlAgent.Tests.csproj --filter "FullyQualifiedName~ChatServiceTests|FullyQualifiedName~StoreMigrationTests|FullyQualifiedName~MessageAttachmentServiceTests"`.**
- [ ] **Step 6: Commit `Persist chat file attachments`.**

### Task 4: Wire ChatTurnService and the LLM attachment handoff

**Files:**
- Modify: `src/SqlAgent.Storage/ChatTurnService.cs`
- Modify: `src/SqlAgent.Storage/NlQueryService.cs`
- Modify: `src/SqlAgent.Storage/ChatService.cs` if the send input seam needs a helper
- Test: `tests/SqlAgent.Tests/ChatTurnServiceTests.cs`
- Test: `tests/SqlAgent.Tests/NlQueryServiceTests.cs`

**Interfaces:**

```csharp
Task<ChatTurnResult> SendAsync(
    Guid? chatId, string question, IReadOnlyList<Guid> databaseIds,
    IReadOnlyList<PendingFileAttachment>? files = null,
    CancellationToken ct = default);
```

`NlQueryService.AskAsync` receives the message file refs as a trailing optional argument and passes only `LlmFileAttachment` values to `LlmSqlRequest`; it does not open streams or read file contents.

- [ ] **Step 1: Write failing tests.** Assert a sent question persists its file references, the gateway sees filename/content type/URL only, no stream is opened during NL query generation, and old calls without files still compile and behave identically.
- [ ] **Step 2: Run the focused ChatTurn/NL tests and verify the missing overload/metadata path.**
- [ ] **Step 3: Thread pending files through `ChatTurnService.SendAsync`, `ChatMessageInput`, and `NlQueryService.AskAsync`.** Keep cancellation behavior and the existing confirmation/error persistence unchanged.
- [ ] **Step 4: Run ChatTurn, NL, and execution tests.**
- [ ] **Step 5: Commit `Pass file metadata through chat turns`.**

### Task 5: Add authenticated hardened file downloads

**Files:**
- Create: `src/SqlAgent.Host/Web/FileDownloadEndpoint.cs`
- Modify: `src/SqlAgent.Host/Program.cs`
- Modify: `src/SqlAgent.Storage/MessageAttachmentService.cs`
- Test: `tests/SqlAgent.Tests/FileDownloadEndpointTests.cs`
- Test: `tests/SqlAgent.Tests/HostSecurityTests.cs` if shared middleware fixtures are required

**Interfaces:**

```csharp
public record FileDownload(Stream Content, string FileName, string ContentType, long SizeBytes);
Task<FileDownload?> OpenDownloadAsync(Guid attachmentId, CancellationToken ct = default);
```

- [ ] **Step 1: Write failing endpoint tests.** Cover missing id 404, authenticated success, unauthenticated rejection through the existing middleware, `Content-Disposition: attachment`, quoted/encoded filename, `X-Content-Type-Options: nosniff`, `Content-Security-Policy: sandbox`, and HTML/SVG content type neutralization.
- [ ] **Step 2: Run the endpoint tests red.**
- [ ] **Step 3: Implement metadata lookup and `MapGet("/files/{id:guid}", ...)` after the existing middleware setup.** Return a stream result without buffering, dispose the stream through the response pipeline, and never include provider exception details in the response.
- [ ] **Step 4: Run endpoint and existing auth tests.**
- [ ] **Step 5: Commit `Add hardened file download endpoint`.**

### Task 6: Add lifecycle cleanup and startup sweep

**Files:**
- Modify: `src/SqlAgent.Storage/ChatService.cs`
- Modify: `src/SqlAgent.Storage/ProjectService.cs`
- Modify: `src/SqlAgent.Storage/StoreInitializer.cs`
- Modify: `src/SqlAgent.Host/Program.cs`
- Test: `tests/SqlAgent.Tests/ChatServiceTests.cs`
- Test: `tests/SqlAgent.Tests/ProjectServiceTests.cs`
- Test: `tests/SqlAgent.Tests/StoreInitializerTests.cs`

- [ ] **Step 1: Write failing lifecycle tests.** Assert chat deletion and project `DeleteChats` remove metadata and request provider deletion; `KeepChats` leaves metadata; provider deletion failure is logged/does not fail metadata deletion; startup sweep runs after migrations and respects the 24-hour floor.
- [ ] **Step 2: Run lifecycle tests red.**
- [ ] **Step 3: Add an attachment cleanup seam that loads storage keys before cascade deletion and performs best-effort provider deletion.** Keep `ProjectDeleteMode.KeepChats` behavior unchanged.
- [ ] **Step 4: Invoke orphan sweep once during host startup after `StoreInitializer.InitializeAsync` and before serving requests.** A sweep failure logs a warning and does not make a healthy database unavailable.
- [ ] **Step 5: Run Chat, Project, StoreInitializer, and migration tests.**
- [ ] **Step 6: Commit `Clean up file attachment blobs`.**

### Task 7: Integrate file picker, pending chips, and sent-message chips

**Files:**
- Modify: `src/SqlAgent.Host/Components/Shared/Chat/AttachmentMenu.razor`
- Modify: `src/SqlAgent.Host/Components/Shared/Chat/AttachmentChips.razor`
- Modify: `src/SqlAgent.Host/Components/Shared/Chat/Composer.razor`
- Modify: `src/SqlAgent.Host/Components/Pages/ChatPage.razor`
- Modify: `src/SqlAgent.Host/Components/Shared/Chat/UserMessage.razor`
- Create: `src/SqlAgent.Host/Components/Shared/Chat/FileAttachmentChip.razor` if shared markup becomes non-trivial
- Modify: `src/SqlAgent.Host/Components/Shared/Chat/AttachmentMenu.razor.css`, `AttachmentChips.razor.css`, and relevant page CSS
- Test: `tests/SqlAgent.Tests/AttachmentMenuTests.cs`
- Test: `tests/SqlAgent.Tests/ChatPageTests.cs`
- Test: `tests/SqlAgent.Tests/ComposerTests.cs`

**Interfaces:**

```csharp
EventCallback<IReadOnlyList<PendingFileAttachment>> OnFilesChanged
```

- [ ] **Step 1: Write failing bUnit tests.** Cover Files menu empty state, upload success chip, remove pending chip, 25 MiB rejection, 11th-file rejection, send persistence, failed Send retaining pending files, and sent-message chips being read-only download links.
- [ ] **Step 2: Run UI tests red.**
- [ ] **Step 3: Add the file input and `InputFile` change handler.** Stream each selected file through `FileStorageService` using the configured max size, show an upload/busy state, and convert failures into `file_too_large`/`file_rejected` copy without provider text.
- [ ] **Step 4: Keep pending files in `ChatPage` circuit state and pass them to `ChatTurnService.SendAsync`.** Clear them only after the turn is persisted; on component disposal call pending cleanup best-effort.
- [ ] **Step 5: Render sent file refs as read-only chips linking to the authenticated download URL.** Do not render file content inline.
- [ ] **Step 6: Run AttachmentMenu, Composer, ChatPage, and existing message rendering tests.**
- [ ] **Step 7: Commit `Add chat file attachments`.**

### Task 8: Documentation, manual checks, and final verification

**Files:**
- Modify: `docs/web-ui.md`
- Modify: `docs/runbook.md`
- Modify: `README.md`
- Create: `docs/adr/0006-file-storage-provider-boundary.md`
- Test: `tests/SqlAgent.Tests/RestyleRegressionTests.cs` as needed for new UI classes

- [ ] **Step 1: Add manual checklist entries.** Cover file picker, cancel/failed upload, exact 25 MiB boundary, HTML/SVG download behavior, authenticated link, chat deletion, abandoned upload after restart, and 10/11 attachment limits.
- [ ] **Step 2: Document configuration.** Record `SqlAgent:Files:Provider`, `SqlAgent:Files:MaxBytes`, storage root, loopback URL limitation for cloud models, and the hardening behavior of `/files/{id}`.
- [ ] **Step 3: Add the ADR.** Record why metadata is in SQLite, bytes are provider-owned, client names never form paths, and local-disk is intentionally the first provider.
- [ ] **Step 4: Run `dotnet restore SqlAgent.slnx`.**
- [ ] **Step 5: Run `dotnet build SqlAgent.slnx --configuration Release --no-restore`.**
- [ ] **Step 6: Run `dotnet test SqlAgent.slnx --configuration Release --no-build --logger "console;verbosity=minimal"`.**
- [ ] **Step 7: Run `git status --short` and record warnings/advisories accurately.**
- [ ] **Step 8: Commit `Document Phase E file attachments`.**

## Definition of Done

- A selected file uploads into provider storage, appears as a pending chip, and persists with the sent message.
- Files over 25 MiB or over 10 per message are rejected server-side with stable codes.
- `GET /files/{id}` requires the existing session and never serves HTML/SVG inline.
- Deleting chats/projects removes attachment metadata and best-effort blobs; startup sweep removes only old orphan blobs.
- Client filenames never determine storage paths.
- File metadata reaches `LlmSqlRequest` without file bytes being read or copied into the prompt.
- Existing C2–D behavior and the full Release test suite remain green.
