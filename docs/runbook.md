# SQL Agent Runbook

## Console startup

```bash
dotnet run --project src/SqlAgent.Host/SqlAgent.Host.csproj
```

The host initializes the SQLite store and logs the number of configured database
connections. Stop it with `Ctrl+C`.

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

## Local-access token

The named-pipe API and the MCP server share one optional token (CD-76). **It is
off by default**: with nothing configured, any process running as the same user
can call every operation, including the configuration ones. Turn it on by giving
the host an expected token:

```bash
SqlAgent__LocalAuth__Token='a-long-random-string' dotnet run --project src/SqlAgent.Host/SqlAgent.Host.csproj
```

For the Windows service or the systemd unit, set the same variable in the
service environment rather than on a shell line, so the token is not left in
shell history.

The host writes the value into the encrypted secret store at startup. Clients
then present it through the `SQLAGENT_AUTH_TOKEN` environment variable — that
covers both the WPF client and any MCP host. Mismatched or missing tokens get a
stable `unauthorized` error.

Two operational notes:

- Removing `SqlAgent__LocalAuth__Token` does **not** disable authentication. A
  blank setting means "nothing to configure"; the previously stored token stays
  in effect. Delete the `local-auth-token` secret to turn it off.
- On Windows the token is DPAPI current-user scoped like every other secret, so
  the service and the clients must run under the account that stored it.

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
- Provider fixture tests do nothing: confirm the `SQLAGENT_TEST_POSTGRES` and `SQLAGENT_TEST_SQLSERVER` variables are set in the test process.
- Client or IDE host gets `unauthorized` on every call: a local-access token is configured on the host but the client presents none. Export `SQLAGENT_AUTH_TOKEN` for the client process and restart it.
- SQL Server fixture fails to connect: wait for container startup to complete. SQL Server 2022 takes tens of seconds to finish recovery on first start, and it has no healthcheck in the fixture — watch `docker logs` for `Recovery is complete`.
- `docker compose up` fails with `port is already allocated`: something else holds 5432 or 1433. Set `SQLAGENT_TEST_POSTGRES_PORT` / `SQLAGENT_TEST_SQLSERVER_PORT` as above instead of stopping the other service.
- `docker exec` into the SQL Server container fails with a path like `C:/Program Files/Git/opt/mssql-tools18/...`: Git Bash rewrote the container path. Prefix the command with `MSYS_NO_PATHCONV=1`.
