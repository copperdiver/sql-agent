# Phase E configuration-binding follow-up

## Change

The host now resolves `FileStorageOptions` from the `SqlAgent:Files` configuration
section. `SqlAgent:Files:Provider` and `SqlAgent:Files:MaxBytes` bind to the existing
provider-neutral options record, while omitted values retain the documented defaults:
`local-disk`, 25 MiB, and 10 attachments per message. `Program` registers those
resolved options and passes the resolved byte limit to the local-disk provider; the
registered `FileStorageService` consumes the same options instance for provider
selection and upload limits.

No remote provider, UI behavior, lifecycle behavior, or storage layout changed.

## Regression coverage

`FileStorageConfigurationTests` covers explicit provider/max-byte binding and the
documented defaults when the section is absent. Existing local storage/service tests
continue to cover use of the options for provider selection and byte enforcement.

## Verification

- `dotnet test tests/SqlAgent.Tests/SqlAgent.Tests.csproj --configuration Release --filter "FullyQualifiedName~FileStorageConfigurationTests|FullyQualifiedName~FileStorageServiceTests|FullyQualifiedName~LocalDiskFileStorageProviderTests" --no-restore --logger "console;verbosity=minimal"` — 12 passed.
- `dotnet build src/SqlAgent.Host/SqlAgent.Host.csproj --configuration Release --no-restore` — succeeded, 0 warnings, 0 errors.

## Concerns

The repository still reports the pre-existing moderate `AngleSharp` advisory during test restore/build evaluation; this change does not alter package dependencies.
