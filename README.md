# Sql Agent

AI Agent for access to SQL databases with controlled access

## Web UI

Run the host with:

```bash
dotnet run --project src/SqlAgent.Host/SqlAgent.Host.csproj
```

It creates the local SQLite store, then writes a URL with a `?token=` required on the first
request only — e.g. `http://127.0.0.1:5099/?token=...` — to `launch-url.txt` beside that store,
readable only by the account the host runs as. The startup log names the file but never the token.
Open the URL from the file in a browser; the token is exchanged for a session cookie on first use.
The host listens on `127.0.0.1` only, on port 5099 by default
(`SqlAgent:Web:Port` to change it). Details on the three screens, the token, and the manual
regression checklist for the parts automated tests can't reach are in
[`docs/web-ui.md`](docs/web-ui.md).

Override the default `Data Source=sqlagent.db` store with:

```bash
SqlAgent__Storage__ConnectionString="Data Source=/path/to/sqlagent.db" dotnet run --project src/SqlAgent.Host/SqlAgent.Host.csproj
```

Windows service and systemd packaging examples are in `packaging/`. Operator
startup, fixture, and troubleshooting notes are in `docs/runbook.md`.

Setting `SqlAgent__LocalAuth__Token` on the host pins the web UI's launch token to a fixed
value and is shared with the MCP server, which clients present through `SQLAGENT_AUTH_TOKEN`.
Leave it unset and the web UI generates a fresh random token every start instead. Details are
in [`docs/runbook.md`](docs/runbook.md).

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
