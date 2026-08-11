# SQL Agent — Claude Code setup (one page)

Register the existing `SqlAgent.Api.Mcp` stdio server with Claude Code. This is
configuration only: all SQL policy, schema visibility, secrets, row caps, and
audit logging stay in Core. The server exposes exactly three tools:

- `list_databases` — configured connections, with provider and read-only flag.
- `describe_schema` — policy-filtered tables/columns/keys (hidden tables omitted).
- `query_database` — policy-validated, row-capped query execution.

Shared setup details for all IDE hosts are in
[`../../docs/ide-plugin-setup.md`](../../docs/ide-plugin-setup.md).

## Prerequisites

- .NET 10 SDK.
- A SQLite config store with at least one connection (see `docs/runbook.md`).
- Windows for durable secrets — the v1 secret store uses DPAPI. On Linux/macOS
  the server runs but saved connection secrets are not durable yet.

## 1. Build the server (clean stdout matters)

MCP speaks JSON-RPC over stdout, so the config points at the built DLL rather
than `dotnet run` (which would print build output onto the stream):

```bash
dotnet build src/SqlAgent.Api.Mcp/SqlAgent.Api.Mcp.csproj -c Release
```

## 2. Register with Claude Code

**Option A — CLI (local to you):**

```bash
claude mcp add --env SQLAGENT_DB=sqlagent.db --scope local sql-agent -- \
  dotnet "$PWD/src/SqlAgent.Api.Mcp/bin/Release/net10.0/SqlAgent.Api.Mcp.dll"
```

**Option B — commit for the team:** copy [`.mcp.json`](./.mcp.json) to the repo
root. Claude Code picks it up automatically and prompts each user once to
approve it. `${CLAUDE_PROJECT_DIR}` resolves to the repo root, and
`SQLAGENT_DB` defaults to `sqlagent.db` (override by exporting it before launch).

Point `SQLAGENT_DB` at an absolute path if your store lives outside the repo.

**If the server has a local-access token configured** (`SqlAgent:LocalAuth:Token`
— off by default, see [`../../docs/runbook.md`](../../docs/runbook.md)), export
`SQLAGENT_AUTH_TOKEN` with the same value before launching Claude Code. The
committed `.mcp.json` passes it through when set and leaves it empty otherwise.
With the CLI, add `--env SQLAGENT_AUTH_TOKEN=…` alongside `--env SQLAGENT_DB=…`.
Without a matching token every tool returns `unauthorized`.

## 3. Smoke test

1. Run `claude` in the project and check the server is connected: `/mcp`
   (or `claude mcp list`) should show `sql-agent` connected with three tools.
2. Ask Claude: **"List my databases."** → expect a `list_databases` call
   returning your configured connections.
3. Ask: **"Describe the schema of `<database_id>`."** → `describe_schema`
   returns only policy-visible tables.
4. Ask: **"Run `SELECT 1` against `<database_id>`."** → `query_database`
   returns columns/rows with a row cap and `elapsed_ms`.
5. Negative check: ask to run an `UPDATE`/`INSERT` on a read-only connection, or
   to read a hidden table → Core denies it (`policy_denied_readonly` /
   `policy_denied_hidden_table`). The denial proves enforcement is in Core, not
   the plugin.

## Stable error codes (surfaced unchanged from Core)

`unauthorized`, `invalid_database_id`, `connection_not_found`, `connection_secret_missing`,
`schema_extraction_error`, `policy_denied_readonly`, `policy_denied_hidden_table`,
`execution_timeout`, `execution_canceled`, `execution_error`.

## Troubleshooting

- **Server fails to start / stream errors:** confirm step 1 built Release; the
  config runs the DLL, not `dotnet run`.
- **No databases listed:** the store is empty or `SQLAGENT_DB` points at the
  wrong file. Confirm the path and seed a connection per `docs/runbook.md`.
- **Secrets lost after restart on Linux/macOS:** expected in v1 (DPAPI is
  Windows-only). Use Windows for durable secrets.
- **Every tool returns `unauthorized`:** the server has a token configured and
  `SQLAGENT_AUTH_TOKEN` is missing or wrong. It is read once at process launch,
  so export it and restart Claude Code.
