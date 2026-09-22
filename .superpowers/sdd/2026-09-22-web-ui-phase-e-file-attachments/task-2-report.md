# Task 2 implementer report — secure local-disk storage

## Outcome

Implemented Task 2 in the approved workspace. The implementation is limited to the local-disk provider, storage service/registry support, and focused provider/service tests; it does not add EF dependencies, a `MessageAttachment` entity, persistence changes, host endpoints, lifecycle wiring, or UI changes.

## Changes

- `LocalDiskFileStorageProvider` stores blobs below `<store>/files/yyyy/MM/{guid}{safe-extension}`.
- Uploads generate the GUID before opening the file, use `FileMode.CreateNew`, copy asynchronously, enforce the configured byte ceiling while streaming, clean partial files on failure, and never use the client filename for identity or directories.
- Storage keys are path-confined; missing reads return `null`; deletes are idempotent.
- `FileStorageService` resolves the configured provider, enforces the max-byte limit through a bounded stream, normalizes display names, returns `PendingFileAttachment` with provider/internal locator data unchanged, and maps provider failures to stable caller-safe exceptions while logging details.
- Orphan cleanup is age-gated at 24 hours and uses injectable `IFileStorageReferenceReader` metadata lookup. Only the local provider's enumerated files are considered; referenced or fresh files are retained.
- The existing Core provider-neutral registry remains authoritative to avoid a duplicate public registry type/ambiguous callers. A storage-project registry source file is present for the storage-layer implementation boundary, but its internal helper is not exposed over the Core contract.

## TDD and verification

The focused tests were written first and the initial focused run failed with the expected missing provider/service types. After implementation, focused verification passed:

```text
dotnet test tests/SqlAgent.Tests/SqlAgent.Tests.csproj --filter "FullyQualifiedName~LocalDiskFileStorageProviderTests|FullyQualifiedName~FileStorageServiceTests" --no-restore
Passed! - Failed: 0, Passed: 6, Skipped: 0, Total: 6
```

`git diff --check` passed. The test invocation emits pre-existing analyzer warnings and the existing NU1902 AngleSharp vulnerability warning; no new warning was introduced by these files.

## Concerns / follow-up

- Host DI registration is intentionally left to the host/lifecycle task, per scope.
- The metadata reader seam is intentionally callback/interface based so Task 3 can bind it to SQLite without adding an EF dependency here.
- The service preserves the provider-returned internal URL/locator; authenticated URL construction remains a later metadata/endpoint responsibility.

## Fix round 1 — registry/configuration failure boundary

Reviewer finding fixed: `FileStorageService.UploadAsync` now resolves the configured provider inside the existing safe provider-error boundary, so unknown or throwing registry selection produces the same stable `FileRejectedException` and does not expose provider exception text. `SweepOrphansAsync` now guards provider selection with the same non-throwing/logging behavior used for sweep provider failures, while preserving cancellation behavior and the injectable metadata-reader seam.

Added focused regression tests for unknown and throwing provider selection in both upload and orphan sweep paths.

Verification:

```text
dotnet test tests/SqlAgent.Tests/SqlAgent.Tests.csproj --filter "FullyQualifiedName~FileStorageServiceTests|FullyQualifiedName~LocalDiskFileStorageProviderTests" --no-restore
Passed!  - Failed:     0, Passed:    10, Skipped:     0, Total:    10, Duration: 138 ms
```

The command emits the pre-existing NU1902 AngleSharp vulnerability warning; no new warning was introduced by this fix.
