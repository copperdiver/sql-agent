# ADR 0003: Local named-pipe JSON contract for the Windows client

## Status

Accepted

## Context

CD-51 needs an admin/query surface for the WPF Windows client that covers
configuration operations (add/edit/remove connections, table policies) as well
as schema description and query execution — a broader scope than the MCP
developer-tool API (ADR-0001). The client runs on the same machine as the Core
daemon.

If the client talked to databases directly, or if Core entities and ADO.NET
drivers leaked into the client, the WPF app would become a second place where
SQL policy, secret handling, and execution could drift out of sync with Core.
The Core must stay the single security boundary.

## Decision

The WPF client communicates with Core over a local named pipe using a
versioned, newline-delimited JSON contract. One compact request object per
line, one response object per line.

The contract lives in `SqlAgent.Api.Local.Contracts` and depends only on
primitives — never on Core entities or WPF view models — so either side can
evolve independently. Key properties:

- A `Version` constant (currently `1`) is echoed in every response so clients
  can gate on breaking wire changes.
- JSON is snake_case, web-defaults, enums as strings; nulls omitted.
- Requests are `{ op, params }`; responses are an envelope with exactly one of
  `data` / `error`. Envelope-level error codes are stable, and
  operation-specific codes (`policy_denied_*`, `execution_*`) pass through from
  Core verbatim so the client sees one stable code set.
- Secrets are write-only: a connection string is accepted on save but never
  returned by any read DTO (`HasSecret` flag only).
- Pipe security is default (current user / local) — the local-only trust
  boundary, not an auth layer.

Operations: list/get/save/delete database, test connection, describe schema,
execute SQL, ask_database (NL query), and list/set table policies. All delegate
to the same Core/Storage services the MCP surface uses, so read-only and
visibility enforcement stays server-side and shared.

## Consequences

- The WPF client stays thin and driver-free; no database provider, policy, or
  secret logic is duplicated client-side.
- Core remains the sole security boundary; both API surfaces enforce identical
  rules because they share the underlying services.
- Wire changes are explicit and versioned, decoupled from UI and Core schema
  evolution.
- The pipe relies on local-machine trust rather than transport authentication;
  if authenticated local access is required, token validation is layered on
  separately (tracked under CD-51 Story 1.7 / CD-76), not by widening this
  contract.

## References

- `src/SqlAgent.Api.Local.Contracts/LocalApiContracts.cs` — DTOs and envelope.
- `src/SqlAgent.Api.Local/NamedPipeApiServer.cs` — pipe host and trust boundary.
- CD-51 Story 1.1; CD-50 T8.
