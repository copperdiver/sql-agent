# Codex CLI setup

Codex CLI supports MCP servers directly. The SQL Agent Codex v1 integration is a
configuration entry that launches `SqlAgent.Api.Mcp`; any future Codex package
should still wrap the same MCP server. Shared prerequisites, tool contracts,
errors, and troubleshooting live in [`ide-plugin-setup.md`](ide-plugin-setup.md).

## Publish a self-contained executable

For a packaged install that does not depend on a .NET SDK on the host, publish
`SqlAgent.Api.Mcp` as a single self-contained executable. Pick the runtime
identifier for your machine (`win-x64`, `linux-x64`, `osx-arm64`, ...):

```bash
dotnet publish src/SqlAgent.Api.Mcp/SqlAgent.Api.Mcp.csproj \
  -c Release -r win-x64 --self-contained
```

The executable lands at
`src/SqlAgent.Api.Mcp/bin/Release/net10.0/win-x64/publish/SqlAgent.Api.Mcp(.exe)`.
Copy that single file wherever you like and point Codex at it. To launch from
source instead, build the DLL (`dotnet build ... -c Release`) and run it with
`dotnet` as shown in the alternate snippets below.

## Configuration

Add `sql-agent` to user-level `~/.codex/config.toml` or a project-level
`.codex/config.toml`, pointing `command` at the published executable:

```toml
[mcp_servers.sql-agent]
command = "C:\\path\\to\\SqlAgent.Api.Mcp.exe"
env = { SQLAGENT_DB = "C:\\path\\to\\sqlagent.db" }
```

Or register it with the CLI:

```bash
codex mcp add sql-agent --env SQLAGENT_DB=/absolute/path/to/sqlagent.db -- \
  /absolute/path/to/SqlAgent.Api.Mcp
```

Running from source instead of a published exe? Use `command = "dotnet"` with
the built DLL as the argument:

```toml
[mcp_servers.sql-agent]
command = "dotnet"
args = ["src/SqlAgent.Api.Mcp/bin/Release/net10.0/SqlAgent.Api.Mcp.dll"]
cwd = "C:\\path\\to\\sql-agent"
env = { SQLAGENT_DB = "C:\\path\\to\\sqlagent.db" }
```

```bash
codex mcp add sql-agent -- dotnet src/SqlAgent.Api.Mcp/bin/Release/net10.0/SqlAgent.Api.Mcp.dll
```

## Local-access token

If the server has a local-access token configured (off by default — see
[`ide-plugin-setup.md`](ide-plugin-setup.md#local-access-token-sqlagent_auth_token)),
add it to the same `env` table:

```toml
[mcp_servers.sql-agent]
command = "C:\\path\\to\\SqlAgent.Api.Mcp.exe"
env = { SQLAGENT_DB = "C:\\path\\to\\sqlagent.db", SQLAGENT_AUTH_TOKEN = "a-long-random-string" }
```

Or with the CLI: `codex mcp add sql-agent --env SQLAGENT_AUTH_TOKEN=… …`.

The token is read once when Codex launches the server, so restart Codex after
changing it. Without a matching token every tool returns `unauthorized`.

## `SQLAGENT_DB` and the policy boundary

`SQLAGENT_DB` points the server at the local SQLite daemon store — the same
config file the SQL Agent daemon and WPF client use (ADR-0004). Always give it
an absolute path so Codex finds the right store regardless of its working
directory; if unset, the server falls back to `sqlagent.db` in the process
directory.

Codex never talks to a database directly. Every call goes through
`SqlAgent.Api.Mcp` into Core, which enforces read-only connections, table
visibility, row caps, and audit logging before any result returns. Writes on a
read-only connection are denied with `policy_denied_readonly`, and hidden tables
are omitted from `describe_schema` and denied with `policy_denied_hidden_table`.
The config entry is launch wiring only; it cannot widen what Core permits.

## Smoke test

1. Start a recent Codex CLI version that supports MCP config loading.
2. Confirm the `sql-agent` MCP server is loaded in the session.
3. Ask Codex to list databases and confirm it calls `list_databases`.
4. Ask it to describe a returned database id and confirm hidden tables are
   absent from `describe_schema`.
5. Ask it to run `SELECT 1` and confirm `query_database` returns rows and
   metadata.
6. On a read-only connection, ask it to run an `UPDATE`. Core should deny the
   call with `policy_denied_readonly`.

## Unsupported paths

Do not build a separate Codex query adapter for v1. Direct MCP configuration is
the supported path for this release. If an older Codex build does not load MCP
servers in a particular mode, upgrade Codex and rerun the smoke test before
filing a SQL Agent issue.
