# Web UI Phase E — File Attachments Design

**Status:** approved for implementation

## Goal

Add secure local file attachments to the existing chat composer without coupling the UI, chat persistence, or future LLM providers to a filesystem layout.

## Scope

Phase E is a local-disk vertical slice. It includes the provider boundary, SQLite metadata, upload and download flows, composer chips, lifecycle cleanup, LLM request metadata, tests, and operator documentation. It does not add a remote storage provider or a real LLM provider.

The existing database attachment flow remains unchanged. Files are a second attachment kind and use the same composer menu/chip row, but they have different persistence and cleanup rules because bytes exist outside SQLite.

## Architecture

### Storage boundary

`SqlAgent.Core/Files` owns provider-neutral contracts:

```csharp
public interface IFileStorageProvider
{
    string Key { get; }
    Task<StoredFile> SaveAsync(FileUpload upload, CancellationToken ct = default);
    Task<Stream?> OpenReadAsync(string storageKey, CancellationToken ct = default);
    Task<bool> DeleteAsync(string storageKey, CancellationToken ct = default);
}

public record StoredFile(string StorageKey, string Url, long SizeBytes);
public record FileUpload(string FileName, string ContentType, Stream Content);
```

`IFileStorageProviderRegistry` resolves the configured provider key. The host registers `local-disk` and reads `SqlAgent:Files:Provider`, defaulting to `local-disk`.

`LocalDiskFileStorageProvider` lives in Storage. Its root is `{store directory}/files`, and each upload is stored under `yyyy/MM/{guid}{extension}`. The extension is derived from the client name only as a display-compatible suffix; the name never determines a directory or the identity of a blob. The provider creates the directory with the same account-only permissions used for the launch URL file and writes with async streaming.

### Pending uploads and message metadata

Uploads happen when the browser selects a file so size and storage failures are visible before Send. The circuit keeps a pending upload record containing the returned provider key, display name, content type, size, URL, and provider key.

`MessageAttachment` is persisted only when the user sends the message:

```text
Id, ChatMessageId, FileName, ContentType, SizeBytes,
ProviderKey, StorageKey, Url, CreatedAt
```

The entity has an index on `ChatMessageId` and cascades with its owning `ChatMessage`. `ChatMessageView` exposes a read-only file reference list; it does not expose file bytes or arbitrary storage paths.

`ChatTurnService.SendAsync` accepts file references alongside database ids. `ChatService.AppendMessageAsync` writes attachment rows in the same transaction as the message. The service enforces at most 10 files per message and the provider/service enforces `SqlAgent:Files:MaxBytes` (default 25 MiB), so browser picker attributes are only a convenience and never the security boundary.

### Download endpoint

The host maps `GET /files/{id}` after the existing origin/token middleware. The endpoint resolves the `MessageAttachment` row, selects its registered provider, opens the stream, and returns it only as a download:

- `Content-Disposition: attachment` with an encoded, quoted display filename;
- `X-Content-Type-Options: nosniff`;
- `Content-Security-Policy: sandbox`;
- `application/octet-stream` for HTML, SVG, XML, and other browser-active content types;
- the stored content type only for inert download types.

There is no inline rendering path. A missing row, missing provider, or missing blob returns 404 without exposing storage details.

### Cleanup and lifecycle

Deleting a chat, deleting a project with `DeleteChats`, or deleting a message removes metadata through the normal cascade and best-effort deletes each blob through its provider. Provider failures are logged and do not turn a successful local metadata delete into a broken UI; startup sweep is the recovery path.

The host runs a startup orphan sweep under the configured files root. It considers only blobs older than 24 hours, extracts no user-controlled path, and deletes files whose provider key/storage key has no matching `MessageAttachment` row. A pending upload that is abandoned by a circuit is deleted immediately on component disposal when possible; the age floor prevents the startup sweep from racing a live upload.

### LLM handoff

`LlmSqlRequest` gains a trailing, defaulted `IReadOnlyList<LlmFileAttachment>` property. `NlQueryService` copies persisted/current file references into this request without opening the files. The placeholder gateway ignores the metadata and still returns `llm_not_configured`; a future locally hosted model may fetch the loopback URLs, while a cloud gateway will need a remote provider later.

## UI flow

`AttachmentMenu` gains a Files section with a hidden file input and an explicit Uploading/failed state. Accepted files appear as removable chips beside database chips. Existing database chips and attachment behavior remain intact.

The composer disables Send while a selected file is uploading, shows a safe user-facing `file_too_large` or `file_rejected` outcome, and clears pending file state only after the message is successfully persisted. A failed Send keeps the pending upload available for retry. Sent message file chips are read-only and link to `/files/{id}` as downloads.

## Stable errors

| Code | Meaning |
|---|---|
| `file_too_large` | A file exceeds `SqlAgent:Files:MaxBytes`. |
| `file_rejected` | Count cap, unreadable stream, invalid input, or storage failure. |

Provider exception text is logged but never returned to the browser. File names are display data and are HTML-encoded by normal Blazor rendering.

## Testing

- Core/storage unit tests cover path identity, extension handling, byte limits, stream copy, missing blobs, and registry selection.
- Storage tests cover attachment metadata round-trip, 10-file limit, cascade deletion, project deletion modes, orphan sweep age floor, and migration from the previous schema.
- Host integration tests cover authenticated download, 404 behavior, download disposition, neutralized active content type, `nosniff`, and CSP headers.
- bUnit tests cover picker/menu/chip rendering, upload failure, send persistence, retry state, and read-only sent chips.
- NL query tests verify file metadata reaches `LlmSqlRequest` and no file bytes are read by the gateway boundary.
- The manual checklist covers selecting a file, canceling selection, downloading HTML/SVG, deleting a chat, restarting after an abandoned upload, and the 25 MiB boundary.

## Explicit non-goals

- No inline image/document preview.
- No remote/S3/Azure provider.
- No virus scanner or content parser.
- No file content extraction into SQL prompts.
- No real LLM provider.
