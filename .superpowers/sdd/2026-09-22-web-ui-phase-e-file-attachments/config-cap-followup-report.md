# Phase E attachment-cap configuration follow-up

## Change

`FileStorageConfiguration.Resolve` now binds only `SqlAgent:Files:Provider` and
`SqlAgent:Files:MaxBytes` into the host options. `MaxAttachmentsPerMessage` is
not part of the bindable shape, so configuration values for that key—including
malformed values—are ignored and the provider-neutral default cap remains 10.

`Program.cs`, `FileStorageOptions`, the provider registry, and storage/provider
selection were not changed. The existing binder behavior for `MaxBytes` is
preserved: negative values still bind through configuration and malformed
values still raise the same `InvalidOperationException`.

## Regression coverage

`FileStorageConfigurationTests` now proves that a configured attachment cap is
ignored while Provider and MaxBytes bind, and covers missing-section defaults,
negative MaxBytes, and malformed MaxBytes behavior.

## Verification

- `dotnet test tests/SqlAgent.Tests/SqlAgent.Tests.csproj --configuration Release --filter "FullyQualifiedName~FileStorageConfigurationTests|FullyQualifiedName~FileStorageServiceTests|FullyQualifiedName~LocalDiskFileStorageProviderTests|FullyQualifiedName~FileStorageContractsTests" --no-restore --logger "console;verbosity=minimal"` — 18 passed.
- `dotnet build src/SqlAgent.Host/SqlAgent.Host.csproj --configuration Release --no-restore` — succeeded, 0 warnings, 0 errors.

## Concerns

The focused test command reports the pre-existing moderate `AngleSharp` advisory
during evaluation; this change does not alter package dependencies.
