# Task 3 Report — Persist chat file attachments

Date: 2026-09-22

## Scope

Implemented only Task 3 of Phase E: persisted message attachment metadata, ChatService file-reference round trips, the provider/storage-key boundary, the generated EF migration, and focused persistence/migration tests. No ChatTurn threading, host download endpoint, lifecycle blob deletion, UI, or documentation changes were made.

## Changes

- `src/SqlAgent.Storage/ChatEntities.cs`
  - Added `MessageAttachment` with the specified metadata fields.
  - Added the `ChatMessage.Attachments` navigation collection.
- `src/SqlAgent.Storage/SqlAgentDbContext.cs`
  - Added the `MessageAttachments` set.
  - Configured the `ChatMessage` relationship with an index on `ChatMessageId` and cascade deletion.
- `src/SqlAgent.Storage/ChatService.cs`
  - Added `ChatFileRef` and trailing optional `Files` fields to `ChatMessageView` and `ChatMessageInput`, preserving all prior positional call shapes.
  - Eager-loads attachment metadata on chat reads and writes attachment rows in the same SaveChanges operation as the message.
  - Keeps provider/storage locators off `ChatMessageView`; the internal binding seam retains them for later download/lifecycle consumers.
  - Preserves the ten-file cap and stable `file_rejected` code through `FileRejectedException`.
- `src/SqlAgent.Storage/MessageAttachmentService.cs`
  - Added metadata binding, pending-upload reference creation, and provider/storage reference lookup for the existing orphan-sweep contract.
  - Keeps file bytes outside EF; only metadata is persisted.
- `src/SqlAgent.Storage/Migrations/20260922080131_FileAttachments.cs` and designer/snapshot
  - Generated with `dotnet ef migrations add FileAttachments --project src/SqlAgent.Storage`.
- Tests
  - Added file metadata round-trip, public-boundary, ten/eleven cap, and cascade assertions to `ChatServiceTests`.
  - Added service binding and provider-locator boundary coverage in `MessageAttachmentServiceTests`.
  - Added migration-from-pre-attachment-schema coverage in `StoreMigrationTests`.

## TDD and verification

### RED

Added the focused persistence tests first. The initial focused run failed at compilation with the expected missing `ChatFileRef`, `MessageAttachmentService`, `MessageAttachments`, and `Files` members.

After the minimal model/service implementation but before migration generation, the migration tests failed with EF Core's pending-model-changes warning, confirming the migration was required.

### GREEN

Generated the migration through EF tooling, then ran:

```text
dotnet test tests/SqlAgent.Tests/SqlAgent.Tests.csproj --filter "FullyQualifiedName~ChatServiceTests|FullyQualifiedName~StoreMigrationTests|FullyQualifiedName~MessageAttachmentServiceTests" --no-restore
Passed! - Failed: 0, Passed: 30, Skipped: 0, Total: 30
```

Full verification:

```text
dotnet test tests/SqlAgent.Tests/SqlAgent.Tests.csproj --no-restore
Passed! - Failed: 0, Passed: 685, Skipped: 0, Total: 685
```

`git diff --check` passed.

## Concerns

- The repository continues to emit its existing NU1902 warning for the vulnerable `AngleSharp` 1.2.0 package.
- Existing unrelated xUnit analyzer warnings remain; no new warnings were introduced by the implementation.
- ChatTurnService, host downloads, lifecycle deletion, UI, and docs remain intentionally deferred to their later Phase E tasks.
