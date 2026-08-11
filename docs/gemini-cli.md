# Gemini CLI setup

Gemini CLI supports stdio MCP servers directly, so the SQL Agent integration is
a configuration entry that launches `SqlAgent.Api.Mcp`. Shared prerequisites,
tool contracts, errors, and troubleshooting live in
[`ide-plugin-setup.md`](ide-plugin-setup.md).

## Configuration

Add `sql-agent` to user-level `~/.gemini/settings.json` or a project-level
`.gemini/settings.json`:

```jsonc
{
  "mcpServers": {
    "sql-agent": {
      "command": "dotnet",
      "args": [
        "src/SqlAgent.Api.Mcp/bin/Release/net10.0/SqlAgent.Api.Mcp.dll"
      ],
      "cwd": "/path/to/sql-agent",
      "env": {
        "SQLAGENT_DB": "/absolute/path/to/sqlagent.db",
        "SQLAGENT_AUTH_TOKEN": "$SQLAGENT_AUTH_TOKEN"
      },
      "timeout": 60000,
      "trust": false
    }
  }
}
```

Use an absolute `SQLAGENT_DB` path. For packaged installs, replace `command` and
`args` with the published `SqlAgent.Api.Mcp` executable path.

`SQLAGENT_AUTH_TOKEN` only matters when the server has a local-access token
configured (off by default — see
[`ide-plugin-setup.md`](ide-plugin-setup.md#local-access-token-sqlagent_auth_token)).
Drop the line if you are not using one; keep it and every tool call fails with
`unauthorized` unless the value matches.

## Smoke test

1. Start Gemini CLI from the configured project.
2. Run `/mcp` or `gemini mcp list` and confirm `sql-agent` is connected.
3. Ask Gemini to list databases and confirm it calls `list_databases`.
4. Ask it to describe a returned database id and confirm `describe_schema`
   returns only visible tables.
5. Ask it to run `SELECT 1` against that database id and confirm
   `query_database` returns columns, rows, `row_count`, and `elapsed_ms`.
6. On a read-only connection, ask it to run an `UPDATE`. Core should deny the
   call with `policy_denied_readonly`.

## Unsupported paths

Do not build a Gemini-specific REST adapter for v1. Gemini CLI can consume the
same stdio MCP server used by Claude Code and Codex.
