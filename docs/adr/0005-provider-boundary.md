# ADR 0002: Database provider boundary for SQL Agent v1

## Status

Accepted

## Context

CD-53 adds pluggable database provider support (Microsoft SQL Server and
PostgreSQL) with room to add MySQL, Oracle, and SQLite later without changing
the Core engine. Story 4.3 calls for a clean `IDatabaseProvider` interface so a
new database type requires only an implementation plus DI registration.

The repo already has the intended shape: `IDatabaseProvider`,
`DatabaseProviderType`, `DatabaseProviderRegistry`, a provider-neutral
`DatabaseSchema`, a centralized `SqlPolicyValidator`, `SchemaService`, and
`QueryExecutionService`. SQL Server and PostgreSQL provider projects exist and
are registered in both the Host and MCP entry points.

The open question is where responsibility sits between Core and the providers —
specifically read-only enforcement, table visibility, auditing, and result
shaping — and how to read Story 4.3's acceptance criterion that lists
`ValidateSql` on the provider interface.

## Decision

SQL Agent Core owns provider selection, read-only policy, table-visibility
policy, auditing, and result shaping. Database providers are thin ADO.NET
adapters responsible only for connection testing, provider-specific schema
extraction, and execution of already-approved SQL.

Concretely:

- Provider selection stays per saved connection via
  `DatabaseConnection.ProviderType`, resolved through
  `DatabaseProviderRegistry.Get(providerType)`. No second provider factory is
  introduced.
- Read-only, multi-statement, unsupported-statement, and hidden-table denial
  stay in `SqlAgent.Core.Policy.SqlPolicyValidator`.
- Providers do not embed business rules; they test connections, extract the
  catalog, and run SQL the Core has already approved.
- ADO.NET connection pooling from `Microsoft.Data.SqlClient` and `Npgsql` is
  used as-is; no custom pooling.
- Dialect hints are added at the service/API layer that builds the LLM schema
  description, not by changing provider-neutral table/column records.

### ValidateSql interpretation

Story 4.3 lists `ValidateSql` among the `IDatabaseProvider` members. We do not
place validation on the provider interface. Validation stays centralized in
`SqlPolicyValidator` so policy is enforced once and identically across every
provider. Duplicating validation per provider would let read-only and
visibility rules drift between SQL Server and PostgreSQL — the exact failure
mode this boundary is meant to prevent.

If a literal `ValidateSql` contract is later required, it is satisfied by a thin
Core service (e.g. `ISqlPolicyService.Validate(...)`) that delegates to the same
centralized validator, never by reimplementing checks inside each provider.

## Consequences

- Adding MySQL, Oracle, or SQLite later requires only a provider implementation
  and DI registration; the Core engine is untouched.
- Policy, auditing, and result shaping stay consistent across all providers.
- Provider implementations stay small and independently testable.
- Catalog extraction must always pass through the Core visibility filter so
  hidden table names cannot leak through relationships; keep that invariant.
- The literal per-provider `ValidateSql` member is intentionally not used unless
  the Product Owner changes the requirement, in which case it routes back to the
  centralized validator.
