# Sql Agent

AI Agent for access to SQL databases with controlled access

## Local host

Run the v1 console host with:

```bash
dotnet run --project src/SqlAgent.Host/SqlAgent.Host.csproj
```

The host creates the local SQLite store on startup. Override the default
`Data Source=sqlagent.db` store with:

```bash
SqlAgent__Storage__ConnectionString="Data Source=/path/to/sqlagent.db" dotnet run --project src/SqlAgent.Host/SqlAgent.Host.csproj
```

Windows service and systemd packaging examples are in `packaging/`. Operator
startup, fixture, and troubleshooting notes are in `docs/runbook.md`.

The local API and the MCP server share an optional local-access token. It is
**off by default** — set `SqlAgent__LocalAuth__Token` on the host to require one,
and give clients the same value through `SQLAGENT_AUTH_TOKEN`. Details and the
disable procedure are in [`docs/runbook.md`](docs/runbook.md).

## Claude Code (MCP)

Register the SQL Agent MCP server with Claude Code to query databases from your
editor. Setup, smoke test, and error codes are in
[`packaging/claude-code/README.md`](packaging/claude-code/README.md); the
ready-to-commit config is [`packaging/claude-code/.mcp.json`](packaging/claude-code/.mcp.json).

Shared IDE plugin setup, MCP tool contracts, host matrix, and troubleshooting
are in [`docs/ide-plugin-setup.md`](docs/ide-plugin-setup.md). Host-specific
Gemini CLI and Codex setup pages are in [`docs/gemini-cli.md`](docs/gemini-cli.md)
and [`docs/codex-cli.md`](docs/codex-cli.md).

## Build and tests

```bash
dotnet restore SqlAgent.slnx
dotnet build SqlAgent.slnx --configuration Release --no-restore
dotnet test SqlAgent.slnx --configuration Release --no-build
```

Provider integration tests are opt-in. Start local fixtures from
`tests/fixtures/docker-compose.yml`, export `SQLAGENT_TEST_POSTGRES` and
`SQLAGENT_TEST_SQLSERVER`, then run:

```bash
dotnet test tests/SqlAgent.Tests/SqlAgent.Tests.csproj --filter ProviderIntegrationTests
```
