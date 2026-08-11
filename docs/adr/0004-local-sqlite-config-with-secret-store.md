# ADR 0004: Local SQLite config store with secret-store abstraction

## Status

Accepted

## Context

CD-51 requires the Core to persist multiple database connections, per-table
visibility policies, cached schema descriptions, and query audit logs (Stories
1.1, 1.3, 1.5), and to keep connection-string secrets out of plaintext config.
v1 is local-first and headless; there is no central server or external database
to depend on.

Connection-string secrets must never sit in plaintext alongside the rest of the
configuration, and the components that use a connection should not need to hold
its secret value.

## Decision

Persist all configuration in a local SQLite database via EF Core
(`SqlAgentDbContext`): `DatabaseConnection`, `TablePolicy`, `SchemaCache`,
`QueryAuditLog`, `AppSetting`, and `Secret`. The same SQLite file is shared by
the daemon, the MCP server, and the WPF client.

Secrets are never read or written directly. All secret access goes through the
`ISecretStore` abstraction (`Set` / `Get` / `Delete` by opaque reference);
callers store only the reference, never the value. Two implementations:

- `DpapiSecretStore` (v1 production, Windows-only): encrypts with Windows DPAPI
  at `CurrentUser` scope and stores the cipher blob in the SQLite `Secret`
  table.
- `InMemorySecretStore`: non-persistent, for tests and non-Windows hosts; not
  for production secret storage.

## Consequences

- Deployment is simple: a single local file, no external database or server.
- The config schema is one EF Core model, versionable with migrations.
- Secret handling is pluggable and isolated behind one interface; the rest of
  the system only ever sees references.
- Durable saved secrets are Windows-only in v1. A non-Windows host falls back to
  the in-memory store, so a Linux production daemon has no durable secret
  storage until a non-DPAPI `ISecretStore` is implemented — a known gap tracked
  under CD-51 (CD-78).
- DPAPI uses `CurrentUser` scope with no extra entropy for v1; per-secret
  entropy would be added only if a shared-machine threat model appears.

## References

- `src/SqlAgent.Storage/SqlAgentDbContext.cs` — SQLite config model.
- `src/SqlAgent.Storage/SecretStore.cs` — `ISecretStore`, `DpapiSecretStore`,
  `InMemorySecretStore`.
- CD-51 Stories 1.1, 1.3, 1.5; CD-50.
