# ADR 0006: File storage provider boundary

## Status

Accepted

## Context

Phase E adds file attachments to Chat. The browser needs a small, durable reference that can be
rendered after a reload, while uploaded bytes need lifecycle cleanup and must not inflate the SQLite
store or enter an SQL/LLM prompt. A local-first deployment also needs to work without an object-store
account, and the UI is deliberately a loopback-only single-user service.

The attachment's original filename is untrusted display data. Treating it as a path would permit path
traversal, collisions, and ambiguous cleanup. Conversely, putting provider-specific paths in the chat
model would make a future object-storage provider a schema and UI migration rather than a replacement
behind a boundary.

## Decision

Keep attachment metadata in SQLite as `MessageAttachment` rows owned by the chat message. Metadata
contains the attachment id, display filename, content type, size, provider key, storage key, URL, and
creation time. The row is written with the sent message and cascades with it. Chat views expose only
the safe file reference; file bytes and provider internals do not enter the view model or
`LlmSqlRequest`.

Keep bytes behind `IFileStorageProvider` and select providers through
`IFileStorageProviderRegistry`. The first and only Phase E provider is `local-disk`, rooted at
`<SQLite store directory>/files`, with blobs under `yyyy/MM/{guid}{safe-extension}`. The GUID is
created before the file is opened; a client filename can contribute only a constrained display-
compatible extension and never determines a directory or blob identity. Uploads stream through the
configured maximum (25 MiB by default), and message binding caps a message at 10 files.

The host exposes `GET /files/{id}` only after the existing origin and session middleware. It resolves
the id through metadata and the provider, streams the result, and always returns
`Content-Disposition: attachment`. `X-Content-Type-Options: nosniff` and
`Content-Security-Policy: sandbox` are set; HTML, XHTML, and SVG are neutralized to
`application/octet-stream`. Provider failures and storage paths are not returned to the browser.

The host runs an age-gated orphan sweep after migrations and performs best-effort provider deletion
when chats are deleted. The sweep considers only local-disk blobs older than 24 hours with no matching
metadata, which avoids racing an upload abandoned by a circuit or restart while it may still be
recoverable.

The provider contract names `SqlAgent:Files:Provider` and `SqlAgent:Files:MaxBytes` (defaults
`local-disk` and 25 MiB). A future provider must be explicitly implemented and registered; no remote
provider is implied by the configuration name.

## Consequences

- SQLite remains compact and queryable for chat history while providers own byte durability, cleanup,
  and storage-specific failure behavior.
- Client filenames cannot select paths or overwrite another blob, and provider keys/storage keys are
  not exposed as arbitrary paths by the UI.
- Local-disk is straightforward to deploy and test, but its files share the SQLite store directory's
  operational backup, permissions, and disk-capacity concerns.
- File URLs handed to an LLM are loopback-relative authenticated URLs. A model hosted on this machine
  may fetch them through the same session; a cloud model cannot reach `127.0.0.1`. Cloud attachment
  analysis therefore requires a remote provider and a separate access-token/URL design.
- Deleting metadata can succeed even if provider deletion fails; logs and the startup sweep provide
  best-effort recovery. The 24-hour age floor intentionally trades immediate reclamation for safety.

## References

- `src/SqlAgent.Core/Files/FileStorageContracts.cs` — provider-neutral contracts and defaults.
- `src/SqlAgent.Storage/LocalDiskFileStorageProvider.cs` — local-disk paths, streaming, and limits.
- `src/SqlAgent.Host/Web/FileDownloadEndpoint.cs` — authenticated hardened download behavior.
- `docs/web-ui.md` and `docs/runbook.md` — operator configuration and manual checks.
