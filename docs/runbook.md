# SQL Agent Runbook

## Console startup

```bash
dotnet run --project src/SqlAgent.Host/SqlAgent.Host.csproj
```

The host initializes the SQLite store and logs the web UI's URL, launch token included (see
[Web UI](#web-ui) below). Stop it with `Ctrl+C`.

Override the store path:

```bash
SqlAgent__Storage__ConnectionString='Data Source=/var/lib/sqlagent/sqlagent.db' dotnet run --project src/SqlAgent.Host/SqlAgent.Host.csproj
```

## Windows Service

Publish the host:

```powershell
dotnet publish src/SqlAgent.Host/SqlAgent.Host.csproj -c Release -r win-x64 --self-contained false -o "C:\Program Files\SqlAgent"
.\packaging\windows\install-service.ps1 -PublishPath "C:\Program Files\SqlAgent"
```

Use a Windows service account for production. The v1 persistent secret store uses
Windows DPAPI current-user scope, so the service must keep the same account to
read existing connection secrets.

## systemd

Publish the host:

```bash
dotnet publish src/SqlAgent.Host/SqlAgent.Host.csproj -c Release -r linux-x64 --self-contained false -o /opt/sqlagent
sudo useradd --system --home /var/lib/sqlagent --create-home sqlagent
sudo install -d -o sqlagent -g sqlagent /var/lib/sqlagent
sudo cp packaging/systemd/sqlagent.service /etc/systemd/system/sqlagent.service
sudo systemctl daemon-reload
sudo systemctl enable --now sqlagent
```

The non-Windows v1 host uses the in-memory secret store. Do not rely on it for
durable production secrets until a Linux secret-store implementation is added.

## Web UI

The host serves the web UI itself — there is no separate client to launch. Start it and open
the URL it logs:

```bash
dotnet run --project src/SqlAgent.Host/SqlAgent.Host.csproj
```

```
info: SQL Agent UI: http://127.0.0.1:5099 — open the URL (token included) written to /var/lib/sqlagent/launch-url.txt
```

`SqlAgent:Web:Port` (`SqlAgent__Web__Port` as an environment variable) picks the TCP port; it
defaults to **5099**. The bind address is fixed at `127.0.0.1` and is not configurable — see
`docs/web-ui.md` for why. Full screen-by-screen coverage, the manual regression checklist, and
the export/chat behavior are in [`docs/web-ui.md`](web-ui.md).

### The launch token

#### Where to read it

The host writes the full `http://127.0.0.1:<port>/?token=…` URL to **`launch-url.txt` in the same
directory as the SQLite store** — `/var/lib/sqlagent/launch-url.txt` for the systemd unit above,
next to `sqlagent.db` for a console run, and whatever `SqlAgent__Storage__ConnectionString` points
at otherwise. It is rewritten on every start.

```bash
sudo cat /var/lib/sqlagent/launch-url.txt   # systemd
cat launch-url.txt                          # console run, from the working directory
```

```powershell
Get-Content "C:\ProgramData\SqlAgent\launch-url.txt"
```

The file is created before anything is written into it and is restricted to the account the host
runs as: mode `600` on Linux/macOS, and on Windows an explicit ACL with inheritance switched off
granting that account and `BUILTIN\Administrators` only. Under the Windows service that means the
service account and an elevated administrator; under systemd it means `sqlagent` and `root`, so
`sudo` is needed to read it.

**The token is not written to any log, at any level.** The named pipe it replaced was ACL'd to a
single user; a loopback TCP port is reachable by every local account and every process, so this
token is now the whole trust boundary — and the log providers this host attaches are not private to
the service account. `AddWindowsService()` attaches the Windows Event Log; `AddSystemd()` sends
stdout to the journal, whose readers on most distributions include `root`, `systemd-journal`, and
`adm`. As a second layer, `appsettings.json` caps the **EventLog** provider at `Warning`, so nothing
logged at `Information` can reach the Event Log even if a future change tries to put it there. That
also means routine `Information` lifetime messages no longer appear in the Event Log; use the
journal, the console, or a file sink for those.

If the file cannot be written the host logs an error (without the token) and still starts. A
*generated* token then has no retrieval path — set `SqlAgent:LocalAuth:Token` yourself, as below,
or make the store directory writable.

#### Where its value comes from

The `?token=` query parameter is presented once; the server exchanges it for a session cookie, so
subsequent requests in that browser don't need it again. Where the token's value comes from depends
on configuration:

- **`SqlAgent:LocalAuth:Token` set** — the host uses that exact value as the launch token, and
  the MCP server (`SqlAgent.Api.Mcp`) persists the same setting into the shared encrypted secret
  store at its own startup, so the identical token also unlocks MCP tool calls (presented there
  via `SQLAGENT_AUTH_TOKEN`). Because it's a fixed, operator-chosen value, it survives host
  restarts.
- **`SqlAgent:LocalAuth:Token` unset** — the host generates a random 256-bit token at every
  start and keeps it in memory only; it is never written to the secret store, it reaches disk only
  as the restricted `launch-url.txt` described above, and it is invalidated the moment the host
  restarts. This is the default and needs no configuration.

For the Windows service or the systemd unit, set `SqlAgent__LocalAuth__Token` in the service
environment rather than on a shell line, so a fixed token is not left in shell history — or leave
it unset and read the generated one from `launch-url.txt` after each start.

Two operational notes:

- A **blank** `SqlAgent:LocalAuth:Token` does **not** clear a token the MCP server already
  persisted to its secret store from an earlier configured value — blank means "nothing to
  configure," not "turn it off." Delete the `local-auth-token` secret to actually disable MCP
  authentication.
- On Windows the persisted secret is DPAPI current-user scoped like every other secret, so the
  MCP server and any client presenting `SQLAGENT_AUTH_TOKEN` must run under the account that
  stored it. The web UI's own launch token isn't DPAPI-scoped at all when generated — it lives
  only in the host process's memory and in the file-ACL-protected `launch-url.txt`.

## Provider fixtures

```bash
docker compose -f tests/fixtures/docker-compose.yml up -d
export SQLAGENT_TEST_POSTGRES='Host=localhost;Port=5432;Database=sqlagent;Username=sqlagent;Password=sqlagent_pw'
export SQLAGENT_TEST_SQLSERVER='Server=localhost,1433;Database=master;User Id=sa;Password=SqlAgent_pw1;TrustServerCertificate=True'
dotnet test tests/SqlAgent.Tests/SqlAgent.Tests.csproj --filter ProviderIntegrationTests
docker compose -f tests/fixtures/docker-compose.yml down
```

### Host ports

Developer machines often already run something on 5432 or 1433, and a collision
makes `docker compose up` fail rather than pick another port. Both host ports are
overridable; the connection strings must use the same values:

```bash
export SQLAGENT_TEST_POSTGRES_PORT=55432
export SQLAGENT_TEST_SQLSERVER_PORT=51433
docker compose -f tests/fixtures/docker-compose.yml up -d
export SQLAGENT_TEST_POSTGRES="Host=localhost;Port=$SQLAGENT_TEST_POSTGRES_PORT;Database=sqlagent;Username=sqlagent;Password=sqlagent_pw"
export SQLAGENT_TEST_SQLSERVER="Server=localhost,$SQLAGENT_TEST_SQLSERVER_PORT;Database=master;User Id=sa;Password=SqlAgent_pw1;TrustServerCertificate=True"
```

Unset, they default to 5432 and 1433. Keep the same values exported for `down`,
otherwise Compose resolves a different port mapping than the one it started.

Any reachable Postgres or SQL Server works instead of the fixtures — point the
two connection strings at it. The tests create their own `"CD-69 Sales"` and
`sqlagent_ct` schemas and drop them in a `finally`, and every assertion is scoped
to those schemas, so an existing database is not disturbed.

## Troubleshooting

- Host exits on startup: confirm the `SqlAgent__Storage__ConnectionString` path exists and the service account can write to it.
- Windows service cannot read saved secrets: confirm it is running as the same Windows account that created them.
- Browser gets a `401` opening the web UI: the URL was opened without its `?token=...` query
  parameter — most often from typing or bookmarking the bare address, or reopening it in a
  window that never presented the token to get a session cookie. Re-read `launch-url.txt` in the
  store directory and open the full URL from there, token included. A bookmark saved from a
  previous run will not work either: a generated token changes on every restart.
- `launch-url.txt` is missing or unreadable: check the startup log for the error naming the
  directory the host tried to write. Either make it writable, or set `SqlAgent__LocalAuth__Token`
  to a value you choose so the token no longer has to be discovered at all.
- Provider fixture tests do nothing: confirm the `SQLAGENT_TEST_POSTGRES` and `SQLAGENT_TEST_SQLSERVER` variables are set in the test process.
- Client or IDE host gets `unauthorized` on every call: a local-access token is configured on the host but the client presents none. Export `SQLAGENT_AUTH_TOKEN` for the client process and restart it.
- SQL Server fixture fails to connect: wait for container startup to complete. SQL Server 2022 takes tens of seconds to finish recovery on first start, and it has no healthcheck in the fixture — watch `docker logs` for `Recovery is complete`.
- `docker compose up` fails with `port is already allocated`: something else holds 5432 or 1433. Set `SQLAGENT_TEST_POSTGRES_PORT` / `SQLAGENT_TEST_SQLSERVER_PORT` as above instead of stopping the other service.
- `docker exec` into the SQL Server container fails with a path like `C:/Program Files/Git/opt/mssql-tools18/...`: Git Bash rewrote the container path. Prefix the command with `MSYS_NO_PATHCONV=1`.
