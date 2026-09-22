# Task 1 Report — Define file storage contracts

Date: 2026-09-22

## Scope

Implemented only Task 1 of Phase E: provider-neutral Core file storage contracts and limits, plus source-compatible trailing attachment metadata on `LlmSqlRequest`. No EF, host, Storage, provider, URL construction, persistence, upload, download, or UI dependencies were added.

## Changed files

- `src/SqlAgent.Core/Files/FileStorageContracts.cs`
  - Added `IFileStorageProvider` and `IFileStorageProviderRegistry`.
  - Added `FileStorageProviderRegistry`, resolving providers by key and throwing the stable `NotSupportedException` message for unknown keys.
  - Added `FileUpload`, `StoredFile`, `LlmFileAttachment`, and `FileStorageOptions` records with the specified defaults (`local-disk`, 25 MiB, 10 attachments).
- `src/SqlAgent.Core/Llm/LlmGateway.cs`
  - Added optional trailing attachment metadata to `LlmSqlRequest` while retaining the existing three-argument constructor call shape.
  - Normalizes omitted/null attachment input to an empty read-only list; no file bytes are represented.
- `tests/SqlAgent.Tests/FileStorageContractsTests.cs`
  - Added focused contract tests for registry resolution, stable unknown-key failure, default limits, and three-argument request compatibility.
- `tests/SqlAgent.Tests/NlQueryServiceTests.cs`
  - Added a focused assertion that existing NL requests carry no file attachments.

## TDD command output

### RED

Command:

```text
dotnet test tests\\SqlAgent.Tests\\SqlAgent.Tests.csproj --filter "FullyQualifiedName~FileStorageContractsTests|FullyQualifiedName~NlQueryServiceTests" --no-restore
```

Result: expected compilation failure because `IFileStorageProvider`, `FileUpload`, and `StoredFile` did not yet exist (`CS0246`).

### GREEN / focused verification

Command:

```text
dotnet test tests\\SqlAgent.Tests\\SqlAgent.Tests.csproj --filter "FullyQualifiedName~FileStorageContractsTests|FullyQualifiedName~NlQueryServiceTests"
```

Result:

```text
Passed!  - Failed:     0, Passed:    19, Skipped:     0, Total:    19, Duration: 3 s - SqlAgent.Tests.dll (net10.0)
```

The command emitted the repository's existing package vulnerability warning for `AngleSharp` 1.2.0 and existing xUnit analyzer warnings in unrelated tests; there were no test failures or new build errors.

## Concerns

- The repository still reports the pre-existing `AngleSharp` moderate vulnerability warning and unrelated xUnit analyzer warnings during test compilation.
- The task intentionally leaves provider storage URL construction and all EF/host integration to later tasks.
