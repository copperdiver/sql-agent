# Local Web UI Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the Windows-only WPF client with a Blazor Server UI served on loopback by the daemon, at parity with WPF plus natural-language queries, a highlighted SQL editor, a schema browser, and result export.

**Architecture:** `SqlAgent.Host` becomes an ASP.NET Core app hosting Blazor Server components. Components call the existing `SqlAgent.Storage` services directly through a per-action DI scope. The named pipe, both `SqlAgent.Api.Local*` projects, and the WPF client are deleted.

**Tech Stack:** .NET 10, ASP.NET Core, Blazor Server (`InteractiveServer`), CodeMirror 5 (vendored, no build step), xUnit, bUnit, `WebApplicationFactory`.

**Spec:** `docs/superpowers/specs/2026-08-11-local-web-ui-design.md`

## Global Constraints

- Target framework `net10.0` for every project. No `net10.0-windows` may remain in the solution.
- **No Node, npm, or any JavaScript build step.** JS dependencies are vendored as pre-built files. This is why CodeMirror **5** is used, not 6 — v6 ships ES modules that require a bundler.
- The HTTP listener binds `127.0.0.1` only. Port from `SqlAgent:Web:Port`, default `5099`.
- Never render exception text in the UI — it can contain a connection string. Log it server-side instead.
- Expected failures (policy denials, timeouts, missing secret, unconfigured LLM) are values with stable codes, not exceptions. Render them as content.
- **Every task that adds behavior is TDD:** write the failing test, run it, watch it fail for the right
  reason, then implement. Four tasks are exempt because no failing test is possible for them, and each
  says so at its own step: Task 1 (deleting projects and scaffolding a host), Task 4 (DI registrations),
  Task 9 (JavaScript interop — bUnit has no JS engine, so the editor is verified manually in Task 11),
  and Task 11 (documentation and packaging). Reviewers should treat these four as compliant; every other
  task must show the red-then-green cycle.
- Commit at the end of every task. Never use `--no-verify`.
- All code, comments, and docs in English (matches the existing repository).

---

## File Structure

**Deleted**

- `src/SqlAgent.Client.Wpf/` (whole project)
- `src/SqlAgent.Api.Local/` (whole project)
- `src/SqlAgent.Api.Local.Contracts/` (whole project)
- `tests/SqlAgent.Tests/LocalApiDispatcherTests.cs`, `tests/SqlAgent.Tests/NamedPipeApiServerTests.cs`
- `docs/wpf-client.md`

**Created in `src/SqlAgent.Host/`**

| Path | Responsibility |
|---|---|
| `Web/LoopbackUrl.cs` | Resolves the listen URL from configuration. Pure function, unit-testable. |
| `Web/LaunchToken.cs` | Holds the process's expected token and whether it came from config or was generated. |
| `Web/LocalOriginMiddleware.cs` | Rejects requests with a foreign `Host` or `Origin`. |
| `Web/TokenAuthMiddleware.cs` | Exchanges `?token=` for a session cookie; 401 without one. |
| `Web/ScopedRunner.cs` | Runs one action inside a fresh DI scope. |
| `Web/AppState.cs` | Currently selected connection; notifies components on change. |
| `Web/ResultExport.cs` | CSV and JSON serialization of a result set. Pure functions. |
| `Components/App.razor`, `Routes.razor`, `_Imports.razor` | Blazor host plumbing. |
| `Components/Layout/MainLayout.razor` | Header plus the persistent left rail. |
| `Components/Layout/SchemaRail.razor` | Connection picker, table search, schema tree, visibility checkboxes. |
| `Components/Pages/Workspace.razor` | `/` — SQL and Chat tabs. |
| `Components/Pages/Connections.razor` | `/connections` — CRUD and test. |
| `Components/Shared/ResultGrid.razor` | Result table, row count, elapsed, truncation notice, export buttons. |
| `Components/Shared/OutcomeMessage.razor` | Renders an expected failure: message plus its code. |
| `Components/Shared/SqlEditor.razor` | CodeMirror wrapper over `IJSRuntime`. |
| `wwwroot/lib/codemirror/` | Vendored `codemirror.min.js`, `codemirror.min.css`, `sql.min.js`. |
| `wwwroot/js/sql-editor.js` | ~30 lines of interop for the editor. |

**Created in `tests/SqlAgent.Tests/`**

`LoopbackUrlTests.cs`, `LocalOriginMiddlewareTests.cs`, `TokenAuthTests.cs`, `ResultExportTests.cs`, `SchemaRailTests.cs`, `ResultGridTests.cs`, `WorkspaceChatTests.cs`, `WebTestHost.cs` (shared `WebApplicationFactory` fixture).

---

## Task 1: Remove the old client stack and stand up the web host

Nothing compiles between deleting `SqlAgent.Api.Local` and converting the host, so demolition and scaffolding are one task.

**Files:**
- Delete: `src/SqlAgent.Client.Wpf/`, `src/SqlAgent.Api.Local/`, `src/SqlAgent.Api.Local.Contracts/`
- Delete: `tests/SqlAgent.Tests/LocalApiDispatcherTests.cs`, `tests/SqlAgent.Tests/NamedPipeApiServerTests.cs`
- Modify: `SqlAgent.slnx`, `src/SqlAgent.Host/SqlAgent.Host.csproj`, `src/SqlAgent.Host/Program.cs`, `tests/SqlAgent.Tests/SqlAgent.Tests.csproj`
- Create: `src/SqlAgent.Host/Components/App.razor`, `Routes.razor`, `_Imports.razor`, `Components/Layout/MainLayout.razor`, `Components/Pages/Workspace.razor`

**Interfaces:**
- Consumes: nothing.
- Produces: a `WebApplication` in `Program.cs` with all existing services registered; `public partial class Program` so tests can use `WebApplicationFactory<Program>`.

- [ ] **Step 1: Delete the three projects and their tests**

```bash
git rm -r src/SqlAgent.Client.Wpf src/SqlAgent.Api.Local src/SqlAgent.Api.Local.Contracts
git rm tests/SqlAgent.Tests/LocalApiDispatcherTests.cs tests/SqlAgent.Tests/NamedPipeApiServerTests.cs
```

- [ ] **Step 2: Drop them from the solution**

Remove these three lines from `SqlAgent.slnx`:

```xml
    <Project Path="src/SqlAgent.Api.Local.Contracts/SqlAgent.Api.Local.Contracts.csproj" />
    <Project Path="src/SqlAgent.Api.Local/SqlAgent.Api.Local.csproj" />
    <Project Path="src/SqlAgent.Client.Wpf/SqlAgent.Client.Wpf.csproj" />
```

- [ ] **Step 3: Drop the reference from the test project**

Remove this line from `tests/SqlAgent.Tests/SqlAgent.Tests.csproj`:

```xml
    <ProjectReference Include="..\..\src\SqlAgent.Api.Local\SqlAgent.Api.Local.csproj" />
```

- [ ] **Step 4: Convert the host project to the web SDK**

Replace `src/SqlAgent.Host/SqlAgent.Host.csproj` with:

```xml
<Project Sdk="Microsoft.NET.Sdk.Web">

  <ItemGroup>
    <ProjectReference Include="..\SqlAgent.Core\SqlAgent.Core.csproj" />
    <ProjectReference Include="..\SqlAgent.Storage\SqlAgent.Storage.csproj" />
    <ProjectReference Include="..\SqlAgent.Providers.SqlServer\SqlAgent.Providers.SqlServer.csproj" />
    <ProjectReference Include="..\SqlAgent.Providers.Postgres\SqlAgent.Providers.Postgres.csproj" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.Hosting.Systemd" Version="10.0.0" />
    <PackageReference Include="Microsoft.Extensions.Hosting.WindowsServices" Version="10.0.0" />
  </ItemGroup>

  <ItemGroup>
    <None Update="appsettings.json" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>

</Project>
```

Two deliberate changes beyond the SDK swap. The `SqlAgent.Api.Mcp` reference is gone: it was never used, and because the MCP project uses top-level statements it exports a second global `Program` type, which would make `WebApplicationFactory<Program>` ambiguous in Task 3. `Microsoft.Extensions.Hosting` is gone because the web SDK brings it in through the shared framework. `OutputType` is dropped — the web SDK sets it.

- [ ] **Step 5: Rewrite `Program.cs` as a web application**

```csharp
using Microsoft.EntityFrameworkCore;
using SqlAgent.Core;
using SqlAgent.Host.Components;
using SqlAgent.Providers.Postgres;
using SqlAgent.Providers.SqlServer;
using SqlAgent.Storage;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddWindowsService(o => o.ServiceName = "SQL Agent").AddSystemd();

builder.Services.AddDbContext<SqlAgentDbContext>(options =>
    options.UseSqlite(builder.Configuration["SqlAgent:Storage:ConnectionString"] ?? "Data Source=sqlagent.db"));

builder.Services.AddSingleton<IDatabaseProvider, SqlServerProvider>();
builder.Services.AddSingleton<IDatabaseProvider, PostgresProvider>();
builder.Services.AddSingleton<IDatabaseProviderRegistry, DatabaseProviderRegistry>();
builder.Services.AddScoped<DatabaseConnectionService>();
builder.Services.AddScoped<QueryExecutionService>();
builder.Services.AddScoped<ConnectionTester>();
builder.Services.AddScoped<SchemaService>();
builder.Services.AddScoped<TablePolicyService>();
builder.Services.AddScoped<NlQueryService>();
builder.Services.AddScoped<LocalTokenAuthenticator>();

// Fail-closed LLM seam: ask_database resolves to a stable llm_error until a vendor gateway is wired.
builder.Services.AddSingleton<ILlmSqlGateway, UnavailableLlmSqlGateway>();

if (OperatingSystem.IsWindows())
    builder.Services.AddScoped<ISecretStore, DpapiSecretStore>();
else
    builder.Services.AddSingleton<ISecretStore, InMemorySecretStore>();

builder.Services.AddRazorComponents().AddInteractiveServerComponents();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
    await scope.ServiceProvider.GetRequiredService<SqlAgentDbContext>().Database.EnsureCreatedAsync();

app.UseStaticFiles();
app.UseAntiforgery();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

await app.RunAsync();

/// <summary>Placeholder until a real LLM gateway is wired. NlQueryService turns this into a stable,
/// user-safe llm_error, so ask_database is contract-complete and fails closed rather than half-wired.</summary>
internal sealed class UnavailableLlmSqlGateway : ILlmSqlGateway
{
    public Task<LlmSqlResponse> GenerateSqlAsync(LlmSqlRequest request, CancellationToken ct = default) =>
        throw new NotSupportedException("No LLM provider is configured on this server.");
}

/// <summary>Exposed so integration tests can boot the real pipeline via WebApplicationFactory.</summary>
public partial class Program;
```

- [ ] **Step 6: Add the Blazor plumbing**

`src/SqlAgent.Host/Components/_Imports.razor`:

```razor
@using Microsoft.AspNetCore.Components.Routing
@using Microsoft.AspNetCore.Components.Web
@using SqlAgent.Core
@using SqlAgent.Host.Components.Layout
@using SqlAgent.Host.Components.Shared
@using SqlAgent.Host.Web
@using SqlAgent.Storage
```

`src/SqlAgent.Host/Components/App.razor`:

```razor
<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="utf-8" />
    <meta name="viewport" content="width=device-width, initial-scale=1.0" />
    <title>SQL Agent</title>
    <base href="/" />
    <HeadOutlet />
</head>
<body>
    <Routes />
    <script src="_framework/blazor.web.js"></script>
</body>
</html>
```

`src/SqlAgent.Host/Components/Routes.razor`:

```razor
<Router AppAssembly="typeof(Program).Assembly">
    <Found Context="routeData">
        <RouteView RouteData="routeData" DefaultLayout="typeof(MainLayout)" />
    </Found>
    <NotFound>
        <LayoutView Layout="typeof(MainLayout)"><p>Not found.</p></LayoutView>
    </NotFound>
</Router>
```

`src/SqlAgent.Host/Components/Layout/MainLayout.razor`:

```razor
@inherits LayoutComponentBase

<header>
    <strong>SQL Agent</strong>
    <nav><a href="/">Workspace</a> <a href="/connections">Connections</a></nav>
</header>
<main>@Body</main>
```

`src/SqlAgent.Host/Components/Pages/Workspace.razor`:

```razor
@page "/"

<h1>Workspace</h1>
```

- [ ] **Step 7: Verify the solution builds and the suite is green**

Run: `dotnet build SqlAgent.slnx -c Release && dotnet test SqlAgent.slnx -c Release`
Expected: build succeeds; 121 tests pass (144 minus the 23 transport tests deleted in Step 1).

- [ ] **Step 8: Commit**

```bash
git add -A
git commit -m "Replace the WPF client stack with a Blazor Server host"
```

---

## Task 2: Loopback binding and launch token

**Files:**
- Create: `src/SqlAgent.Host/Web/LoopbackUrl.cs`, `src/SqlAgent.Host/Web/LaunchToken.cs`
- Test: `tests/SqlAgent.Tests/LoopbackUrlTests.cs`
- Modify: `src/SqlAgent.Host/Program.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `LoopbackUrl.Resolve(IConfiguration) → string`; `sealed class LaunchToken { string Value; bool IsGenerated; }` registered as a singleton.

- [ ] **Step 1: Write the failing test**

`tests/SqlAgent.Tests/LoopbackUrlTests.cs`:

```csharp
using Microsoft.Extensions.Configuration;
using SqlAgent.Host.Web;

namespace SqlAgent.Tests;

public class LoopbackUrlTests
{
    private static IConfiguration Config(params (string Key, string Value)[] pairs) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(pairs.Select(p => new KeyValuePair<string, string?>(p.Key, p.Value)))
            .Build();

    [Fact]
    public void Resolve_defaults_to_loopback_on_5099()
    {
        Assert.Equal("http://127.0.0.1:5099", LoopbackUrl.Resolve(Config()));
    }

    [Fact]
    public void Resolve_honors_the_configured_port()
    {
        Assert.Equal("http://127.0.0.1:8123", LoopbackUrl.Resolve(Config(("SqlAgent:Web:Port", "8123"))));
    }

    [Fact]
    public void Resolve_always_binds_the_loopback_address()
    {
        // The port is configurable; the host is not. Binding 0.0.0.0 would expose the configuration
        // surface to the network, which is phase 3 work behind TLS and real sessions.
        Assert.StartsWith("http://127.0.0.1:", LoopbackUrl.Resolve(Config(("SqlAgent:Web:Port", "1"))));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("70000")]
    [InlineData("not-a-port")]
    public void Resolve_rejects_a_port_outside_the_valid_range(string port)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => LoopbackUrl.Resolve(Config(("SqlAgent:Web:Port", port))));
        Assert.Contains("SqlAgent:Web:Port", ex.Message);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/SqlAgent.Tests/SqlAgent.Tests.csproj --filter LoopbackUrlTests`
Expected: FAIL to compile — `LoopbackUrl` does not exist.

- [ ] **Step 3: Implement `LoopbackUrl`**

`src/SqlAgent.Host/Web/LoopbackUrl.cs`:

```csharp
namespace SqlAgent.Host.Web;

/// <summary>
/// Resolves the address the host listens on. The port is configurable; the address is not — a loopback
/// bind is what keeps the configuration surface off the network until phase 3 adds TLS and sessions.
/// Kept as a pure function so the binding rule is unit-testable: WebApplicationFactory runs on an
/// in-memory TestServer and opens no socket, so it cannot observe the real binding.
/// </summary>
public static class LoopbackUrl
{
    public const int DefaultPort = 5099;
    public const string ConfigKey = "SqlAgent:Web:Port";

    public static string Resolve(IConfiguration configuration)
    {
        var raw = configuration[ConfigKey];
        if (string.IsNullOrWhiteSpace(raw))
            return $"http://127.0.0.1:{DefaultPort}";

        if (!int.TryParse(raw, out var port) || port is < 1 or > 65535)
            throw new InvalidOperationException($"{ConfigKey} must be a TCP port between 1 and 65535, but was '{raw}'.");

        return $"http://127.0.0.1:{port}";
    }
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test tests/SqlAgent.Tests/SqlAgent.Tests.csproj --filter LoopbackUrlTests`
Expected: PASS, 6 tests.

- [ ] **Step 5: Implement `LaunchToken`**

`src/SqlAgent.Host/Web/LaunchToken.cs`:

```csharp
using System.Security.Cryptography;

namespace SqlAgent.Host.Web;

/// <summary>
/// The token a browser must present once to obtain a session cookie.
///
/// When the operator configured SqlAgent:LocalAuth:Token, that value is used and keeps its existing
/// persisted behavior. Otherwise a random token is generated for this process only and is deliberately
/// NOT written to the secret store: LocalTokenAuthenticator.ConfigureFromSettingAsync persists whatever
/// it is handed, and a blank setting does not clear a stored value — so persisting a per-start token
/// would silently switch authentication on for the MCP server too, with a value that changes on every
/// restart.
/// </summary>
public sealed class LaunchToken
{
    public string Value { get; }
    public bool IsGenerated { get; }

    public LaunchToken(IConfiguration configuration)
    {
        var configured = configuration["SqlAgent:LocalAuth:Token"];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            Value = configured;
            IsGenerated = false;
        }
        else
        {
            Value = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            IsGenerated = true;
        }
    }
}
```

- [ ] **Step 6: Wire both into `Program.cs`**

Add after `builder.Services.AddRazorComponents()...`:

```csharp
builder.Services.AddSingleton<LaunchToken>();
builder.WebHost.UseUrls(LoopbackUrl.Resolve(builder.Configuration));
```

And after `app.MapRazorComponents<App>()...`, before `await app.RunAsync()`:

```csharp
// Print the ready-to-click URL the way Jupyter does: the token is required only for the first request,
// which then exchanges it for a session cookie.
var launchToken = app.Services.GetRequiredService<LaunchToken>();
app.Logger.LogInformation(
    "SQL Agent UI: {Url}/?token={Token}", LoopbackUrl.Resolve(app.Configuration), launchToken.Value);
```

- [ ] **Step 7: Verify the whole suite still passes**

Run: `dotnet test SqlAgent.slnx -c Release`
Expected: PASS.

- [ ] **Step 8: Commit**

```bash
git add -A
git commit -m "Bind the UI to loopback and print a launch token URL"
```

---

## Task 3: Origin validation and token-to-cookie authentication

This is the security core. A loopback HTTP port is reachable from any page in the user's browser, which a named pipe never was.

**Files:**
- Create: `src/SqlAgent.Host/Web/LocalOriginMiddleware.cs`, `src/SqlAgent.Host/Web/TokenAuthMiddleware.cs`
- Test: `tests/SqlAgent.Tests/WebTestHost.cs`, `tests/SqlAgent.Tests/TokenAuthTests.cs`
- Modify: `src/SqlAgent.Host/Program.cs`, `tests/SqlAgent.Tests/SqlAgent.Tests.csproj`

**Interfaces:**
- Consumes: `LaunchToken.Value` from Task 2.
- Produces: middleware registered in `Program.cs`; cookie named `sqlagent_session`.

- [ ] **Step 1: Add the test packages**

Add to `tests/SqlAgent.Tests/SqlAgent.Tests.csproj`:

```xml
    <PackageReference Include="Microsoft.AspNetCore.Mvc.Testing" Version="10.0.0" />
    <PackageReference Include="bunit" Version="1.40.0" />
```

and the host project reference:

```xml
    <ProjectReference Include="..\..\src\SqlAgent.Host\SqlAgent.Host.csproj" />
```

If `bunit` 1.40.0 does not restore against .NET 10, run `dotnet add tests/SqlAgent.Tests package bunit` to take the newest 1.x and use that version.

- [ ] **Step 2: Write the failing tests**

`tests/SqlAgent.Tests/WebTestHost.cs`:

```csharp
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace SqlAgent.Tests;

/// <summary>Boots the real host pipeline in memory with a known token and a throwaway SQLite file.</summary>
public sealed class WebTestHost : WebApplicationFactory<Program>
{
    public const string Token = "test-token-value";

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"sqlagent-test-{Guid.NewGuid():N}.db");

    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.ConfigureHostConfiguration(c => c.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["SqlAgent:LocalAuth:Token"] = Token,
            ["SqlAgent:Storage:ConnectionString"] = $"Data Source={_dbPath}",
        }));
        return base.CreateHost(builder);
    }

    /// <summary>A client that does not follow redirects, so a 302 to the login path is observable.</summary>
    public HttpClient NewClient() => CreateClient(new WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = false,
    });

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }
}
```

`tests/SqlAgent.Tests/TokenAuthTests.cs`:

```csharp
using System.Net;

namespace SqlAgent.Tests;

public class TokenAuthTests : IClassFixture<WebTestHost>
{
    private readonly WebTestHost _host;
    public TokenAuthTests(WebTestHost host) => _host = host;

    [Fact]
    public async Task Request_without_a_token_or_cookie_is_unauthorized()
    {
        var r = await _host.NewClient().GetAsync("/");
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }

    [Fact]
    public async Task Wrong_token_is_unauthorized()
    {
        var r = await _host.NewClient().GetAsync("/?token=nope");
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }

    [Fact]
    public async Task Valid_token_issues_a_session_cookie()
    {
        var r = await _host.NewClient().GetAsync($"/?token={WebTestHost.Token}");

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var cookie = Assert.Single(r.Headers.GetValues("Set-Cookie"));
        Assert.Contains("sqlagent_session=", cookie);
        Assert.Contains("HttpOnly", cookie);
        Assert.Contains("SameSite=Strict", cookie);
    }

    [Fact]
    public async Task Cookie_from_a_previous_exchange_is_accepted_without_the_token()
    {
        var client = _host.NewClient();
        await client.GetAsync($"/?token={WebTestHost.Token}");   // client keeps the cookie

        var r = await client.GetAsync("/");

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }

    [Fact]
    public async Task Foreign_origin_is_rejected_even_with_a_valid_cookie()
    {
        var client = _host.NewClient();
        await client.GetAsync($"/?token={WebTestHost.Token}");

        var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.Add("Origin", "https://evil.example");
        var r = await client.SendAsync(request);

        // Without this, any page the user has open could drive the configuration UI.
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
    }

    [Fact]
    public async Task Foreign_host_header_is_rejected()
    {
        // DNS rebinding: an attacker-controlled name resolving to 127.0.0.1.
        var request = new HttpRequestMessage(HttpMethod.Get, $"/?token={WebTestHost.Token}");
        request.Headers.Host = "evil.example";
        var r = await _host.NewClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test tests/SqlAgent.Tests/SqlAgent.Tests.csproj --filter TokenAuthTests`
Expected: FAIL — every request returns 200 because no middleware exists yet.

- [ ] **Step 4: Implement `LocalOriginMiddleware`**

`src/SqlAgent.Host/Web/LocalOriginMiddleware.cs`:

```csharp
namespace SqlAgent.Host.Web;

/// <summary>
/// Rejects requests whose Host or Origin is not local. A loopback port is reachable from any page the
/// user has open, so without this a hostile site could drive the configuration UI, and a name resolving
/// to 127.0.0.1 could defeat the browser's same-origin rule (DNS rebinding). Runs ahead of everything
/// else, including the Blazor WebSocket negotiation.
/// </summary>
public sealed class LocalOriginMiddleware(RequestDelegate next)
{
    private static readonly string[] LocalHosts = ["127.0.0.1", "localhost", "[::1]"];

    public async Task InvokeAsync(HttpContext context)
    {
        if (!IsLocalHost(context.Request.Host.Host) || !IsLocalOrigin(context.Request.Headers.Origin))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
        await next(context);
    }

    private static bool IsLocalHost(string host) =>
        LocalHosts.Contains(host, StringComparer.OrdinalIgnoreCase);

    private static bool IsLocalOrigin(string? origin)
    {
        // No Origin at all is normal for a top-level navigation, so absence is not a rejection.
        if (string.IsNullOrEmpty(origin)) return true;
        return Uri.TryCreate(origin, UriKind.Absolute, out var uri) && IsLocalHost(uri.Host);
    }
}
```

- [ ] **Step 5: Implement `TokenAuthMiddleware`**

`src/SqlAgent.Host/Web/TokenAuthMiddleware.cs`:

```csharp
using System.Security.Cryptography;
using System.Text;

namespace SqlAgent.Host.Web;

/// <summary>
/// Exchanges a one-time <c>?token=</c> query parameter for a session cookie, and refuses everything else
/// with 401 — including WebSocket upgrades, so an unauthenticated caller cannot open a Blazor circuit.
/// Framework assets are exempt: the browser must be able to load blazor.web.js before it can authenticate.
/// </summary>
public sealed class TokenAuthMiddleware(RequestDelegate next, LaunchToken token)
{
    public const string CookieName = "sqlagent_session";

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Path.StartsWithSegments("/_framework"))
        {
            await next(context);
            return;
        }

        if (context.Request.Cookies.TryGetValue(CookieName, out var cookie) && Matches(cookie))
        {
            await next(context);
            return;
        }

        if (context.Request.Query.TryGetValue("token", out var presented) && Matches(presented))
        {
            context.Response.Cookies.Append(CookieName, token.Value, new CookieOptions
            {
                HttpOnly = true,
                SameSite = SameSiteMode.Strict,
                Secure = false,     // phase 1 is plain HTTP on loopback; phase 3 adds TLS
                IsEssential = true,
            });
            await next(context);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
    }

    private bool Matches(string? presented)
    {
        if (string.IsNullOrEmpty(presented)) return false;
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(presented), Encoding.UTF8.GetBytes(token.Value));
    }
}
```

- [ ] **Step 6: Register both, in order, in `Program.cs`**

Immediately after `var app = builder.Build();` and the `EnsureCreatedAsync` block, before `app.UseStaticFiles()`:

```csharp
// Order matters: origin checks run before anything reads the token, so a hostile page cannot even
// attempt an exchange.
app.UseMiddleware<LocalOriginMiddleware>();
app.UseMiddleware<TokenAuthMiddleware>();
```

- [ ] **Step 7: Run the tests to verify they pass**

Run: `dotnet test tests/SqlAgent.Tests/SqlAgent.Tests.csproj --filter TokenAuthTests`
Expected: PASS, 6 tests.

- [ ] **Step 8: Commit**

```bash
git add -A
git commit -m "Gate the web UI behind origin validation and a launch token"
```

---

## Task 4: Per-action DI scope and selected-connection state

**Files:**
- Create: `src/SqlAgent.Host/Web/ScopedRunner.cs`, `src/SqlAgent.Host/Web/AppState.cs`
- Modify: `src/SqlAgent.Host/Program.cs`

**Interfaces:**
- Produces:
  - `ScopedRunner.RunAsync<TService, TResult>(Func<TService, Task<TResult>>) → Task<TResult>` where `TService : notnull`
  - `ScopedRunner.RunAsync<TService>(Func<TService, Task>) → Task`
  - `AppState { Guid? ConnectionId; DatabaseConnectionInfo? Connection; event Action? Changed; void Select(DatabaseConnectionInfo?) }`
  - Both registered scoped (one per Blazor circuit).

- [ ] **Step 1: Implement `ScopedRunner`**

There is no unit test for this type: it is a three-line wrapper over `IServiceScopeFactory`, and every component test in Tasks 5–10 exercises it end to end. Testing it in isolation would assert that the framework creates scopes.

`src/SqlAgent.Host/Web/ScopedRunner.cs`:

```csharp
namespace SqlAgent.Host.Web;

/// <summary>
/// Runs one user action inside a fresh DI scope.
///
/// A Blazor circuit lives as long as the browser tab — hours. Scoped services are bound to the circuit,
/// so injecting SqlAgentDbContext straight into a component would keep one context alive that whole
/// time, accumulating tracked entities and serving stale reads. Scoping per action keeps the Storage
/// services exactly as they are, which the alternative (moving all seven onto IDbContextFactory) would not.
/// </summary>
public sealed class ScopedRunner(IServiceScopeFactory scopeFactory)
{
    public async Task<TResult> RunAsync<TService, TResult>(Func<TService, Task<TResult>> action)
        where TService : notnull
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<TService>());
    }

    public async Task RunAsync<TService>(Func<TService, Task> action)
        where TService : notnull
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        await action(scope.ServiceProvider.GetRequiredService<TService>());
    }
}
```

- [ ] **Step 2: Implement `AppState`**

`src/SqlAgent.Host/Web/AppState.cs`:

```csharp
using SqlAgent.Storage;

namespace SqlAgent.Host.Web;

/// <summary>
/// Which connection the workspace is pointed at. Scoped to the circuit, so it is per browser tab.
/// The rail, the SQL tab, and the chat tab all read it, so it lives here rather than in a parent
/// component's parameters.
/// </summary>
public sealed class AppState
{
    public DatabaseConnectionInfo? Connection { get; private set; }
    public Guid? ConnectionId => Connection?.Id;

    public event Action? Changed;

    public void Select(DatabaseConnectionInfo? connection)
    {
        if (Connection?.Id == connection?.Id) return;
        Connection = connection;
        Changed?.Invoke();
    }
}
```

- [ ] **Step 3: Register both in `Program.cs`**

```csharp
builder.Services.AddScoped<ScopedRunner>();
builder.Services.AddScoped<AppState>();
```

- [ ] **Step 4: Verify the build**

Run: `dotnet build SqlAgent.slnx -c Release`
Expected: build succeeds.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "Add per-action DI scoping and selected-connection state"
```

---

## Task 5: Connections page

**Files:**
- Create: `src/SqlAgent.Host/Components/Pages/Connections.razor`
- Test: `tests/SqlAgent.Tests/ConnectionsPageTests.cs`

**Interfaces:**
- Consumes: `ScopedRunner`, `AppState` (Task 4); `DatabaseConnectionService`, `ConnectionTester`, `DatabaseConnectionInput`, `DatabaseConnectionInfo`.
- Produces: route `/connections`.

- [ ] **Step 1: Write the failing test**

`tests/SqlAgent.Tests/ConnectionsPageTests.cs`:

```csharp
using Bunit;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SqlAgent.Core;
using SqlAgent.Host.Components.Pages;
using SqlAgent.Host.Web;
using SqlAgent.Storage;

namespace SqlAgent.Tests;

public class ConnectionsPageTests : IDisposable
{
    private readonly SqliteConnection _conn = new("DataSource=:memory:");
    private readonly Bunit.TestContext _ctx = new();

    public ConnectionsPageTests()
    {
        _conn.Open();
        _ctx.Services.AddDbContext<SqlAgentDbContext>(o => o.UseSqlite(_conn));
        _ctx.Services.AddSingleton<ISecretStore, InMemorySecretStore>();
        _ctx.Services.AddSingleton<IDatabaseProvider, PostgresProviderStub>();
        _ctx.Services.AddSingleton<IDatabaseProviderRegistry, DatabaseProviderRegistry>();
        _ctx.Services.AddScoped<DatabaseConnectionService>();
        _ctx.Services.AddScoped<ConnectionTester>();
        _ctx.Services.AddScoped<ScopedRunner>();
        _ctx.Services.AddScoped<AppState>();

        using var scope = _ctx.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<SqlAgentDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public void Saved_connections_are_listed_with_provider_and_read_only_flag()
    {
        SeedAsync("prod-analytics", isReadOnly: true).GetAwaiter().GetResult();

        var page = _ctx.RenderComponent<Connections>();

        Assert.Contains("prod-analytics", page.Markup);
        Assert.Contains("Postgres", page.Markup);
        Assert.Contains("read-only", page.Markup);
    }

    [Fact]
    public void The_connection_string_is_never_rendered_back()
    {
        SeedAsync("prod-analytics", isReadOnly: true).GetAwaiter().GetResult();

        var page = _ctx.RenderComponent<Connections>();

        // The secret is write-only: it goes in, it never comes back out to the browser.
        Assert.DoesNotContain("Password=", page.Markup);
        Assert.DoesNotContain("super-secret", page.Markup);
    }

    private async Task SeedAsync(string name, bool isReadOnly)
    {
        using var scope = _ctx.Services.CreateScope();
        var connections = scope.ServiceProvider.GetRequiredService<DatabaseConnectionService>();
        await connections.CreateAsync(
            new DatabaseConnectionInput(name, DatabaseProviderType.Postgres, isReadOnly),
            "Host=localhost;Password=super-secret");
    }

    public void Dispose()
    {
        _ctx.Dispose();
        _conn.Dispose();
    }
}

/// <summary>Provider double: the page never reaches a real database in these tests.</summary>
file sealed class PostgresProviderStub : IDatabaseProvider
{
    public DatabaseProviderType ProviderType => DatabaseProviderType.Postgres;
    public Task<ConnectionTestResult> TestConnectionAsync(string cs, CancellationToken ct = default)
        => Task.FromResult(ConnectionTestResult.Ok("PostgreSQL 16.0", 12));
    public Task<DatabaseSchema> GetSchemaAsync(string cs, CancellationToken ct = default)
        => Task.FromResult(new DatabaseSchema([]));
    public Task<QueryResultSet> ExecuteQueryAsync(string cs, string sql, QueryExecutionOptions o, CancellationToken ct = default)
        => Task.FromResult(new QueryResultSet([], [], false));
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/SqlAgent.Tests/SqlAgent.Tests.csproj --filter ConnectionsPageTests`
Expected: FAIL to compile — `Connections` does not exist.

- [ ] **Step 3: Implement the page**

`src/SqlAgent.Host/Components/Pages/Connections.razor`:

```razor
@page "/connections"
@rendermode InteractiveServer
@inject ScopedRunner Runner
@inject AppState State

<h1>Connections</h1>

<table>
    <thead><tr><th>Name</th><th>Provider</th><th>Mode</th><th></th></tr></thead>
    <tbody>
        @foreach (var c in _connections)
        {
            <tr>
                <td>@c.Name</td>
                <td>@c.ProviderType</td>
                <td>@(c.IsReadOnly ? "read-only" : "read-write")</td>
                <td>
                    <button @onclick="() => Edit(c)">Edit</button>
                    <button @onclick="() => TestAsync(c.Id)">Test</button>
                    <button @onclick="() => DeleteAsync(c.Id)">Delete</button>
                </td>
            </tr>
        }
    </tbody>
</table>

<h2>@(_editingId is null ? "New connection" : "Edit connection")</h2>

<label>Name <input @bind="_name" /></label>
<label>Provider
    <select @bind="_provider">
        @foreach (var p in Enum.GetValues<DatabaseProviderType>())
        {
            <option value="@p">@p</option>
        }
    </select>
</label>
<label>Read-only <input type="checkbox" @bind="_isReadOnly" /></label>
<label>Connection string
    <input type="password" @bind="_connectionString"
           placeholder="@(_editingId is null ? "" : "leave blank to keep the stored secret")" />
</label>

<button @onclick="SaveAsync">Save</button>
<button @onclick="NewConnection">New</button>

@if (_status is not null)
{
    <p role="status">@_status</p>
}

@code {
    private IReadOnlyList<DatabaseConnectionInfo> _connections = [];
    private Guid? _editingId;
    private string _name = "";
    private DatabaseProviderType _provider = DatabaseProviderType.Postgres;
    private bool _isReadOnly = true;
    private string _connectionString = "";
    private string? _status;

    protected override async Task OnInitializedAsync() => await ReloadAsync();

    private async Task ReloadAsync() =>
        _connections = await Runner.RunAsync<DatabaseConnectionService, IReadOnlyList<DatabaseConnectionInfo>>(
            s => s.ListAsync());

    private void Edit(DatabaseConnectionInfo c)
    {
        _editingId = c.Id;
        _name = c.Name;
        _provider = c.ProviderType;
        _isReadOnly = c.IsReadOnly;
        _connectionString = "";     // never populated from the server; blank means "keep the stored secret"
        _status = null;
    }

    private void NewConnection()
    {
        _editingId = null;
        _name = "";
        _provider = DatabaseProviderType.Postgres;
        _isReadOnly = true;
        _connectionString = "";
        _status = null;
    }

    private async Task SaveAsync()
    {
        var input = new DatabaseConnectionInput(_name, _provider, _isReadOnly);
        if (_editingId is { } id)
        {
            var secret = string.IsNullOrWhiteSpace(_connectionString) ? null : _connectionString;
            await Runner.RunAsync<DatabaseConnectionService>(s => s.UpdateAsync(id, input, secret));
        }
        else
        {
            if (string.IsNullOrWhiteSpace(_connectionString))
            {
                _status = "A connection string is required for a new connection.";
                return;
            }
            await Runner.RunAsync<DatabaseConnectionService>(s => s.CreateAsync(input, _connectionString));
        }
        _connectionString = "";
        _status = "Saved.";
        await ReloadAsync();
    }

    private async Task DeleteAsync(Guid id)
    {
        await Runner.RunAsync<DatabaseConnectionService>(s => s.DeleteAsync(id));
        if (State.ConnectionId == id) State.Select(null);
        if (_editingId == id) NewConnection();
        await ReloadAsync();
    }

    private async Task TestAsync(Guid id)
    {
        var result = await Runner.RunAsync<ConnectionTester, ConnectionTestResult?>(t => t.TestSavedAsync(id));
        // A rejecting-but-reachable server is a normal outcome here, not an error to throw.
        _status = result is null ? "Connection or its secret is missing."
            : result.Success ? $"Connection OK ({result.ServerVersion}, {result.ElapsedMs} ms)"
            : $"Connection failed: {result.Error}";
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/SqlAgent.Tests/SqlAgent.Tests.csproj --filter ConnectionsPageTests`
Expected: PASS, 2 tests.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "Add the connections management page"
```

---

## Task 6: Schema rail with visibility toggles

**Files:**
- Create: `src/SqlAgent.Host/Components/Layout/SchemaRail.razor`
- Modify: `src/SqlAgent.Host/Components/Layout/MainLayout.razor`
- Test: `tests/SqlAgent.Tests/SchemaRailTests.cs`

**Interfaces:**
- Consumes: `ScopedRunner`, `AppState`; `TablePolicyService.ListAsync(Guid) → IReadOnlyList<TableVisibility>?`, `TablePolicyService.SetVisibilityAsync(Guid, string, string, bool)`, `TableVisibility(string Schema, string Table, bool IsVisible)`.
- Produces: the rail rendered inside `MainLayout`.

- [ ] **Step 1: Write the failing test**

`tests/SqlAgent.Tests/SchemaRailTests.cs`:

```csharp
using Bunit;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SqlAgent.Core;
using SqlAgent.Host.Components.Layout;
using SqlAgent.Host.Web;
using SqlAgent.Storage;

namespace SqlAgent.Tests;

public class SchemaRailTests : IDisposable
{
    private readonly SqliteConnection _conn = new("DataSource=:memory:");
    private readonly Bunit.TestContext _ctx = new();
    private Guid _connectionId;

    public SchemaRailTests()
    {
        _conn.Open();
        _ctx.Services.AddDbContext<SqlAgentDbContext>(o => o.UseSqlite(_conn));
        _ctx.Services.AddSingleton<ISecretStore, InMemorySecretStore>();
        _ctx.Services.AddSingleton<IDatabaseProvider, RailProviderStub>();
        _ctx.Services.AddSingleton<IDatabaseProviderRegistry, DatabaseProviderRegistry>();
        _ctx.Services.AddScoped<DatabaseConnectionService>();
        _ctx.Services.AddScoped<TablePolicyService>();
        _ctx.Services.AddScoped<ScopedRunner>();
        _ctx.Services.AddScoped<AppState>();

        using var scope = _ctx.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<SqlAgentDbContext>().Database.EnsureCreated();
        var connections = scope.ServiceProvider.GetRequiredService<DatabaseConnectionService>();
        var created = connections.CreateAsync(
            new DatabaseConnectionInput("c", DatabaseProviderType.Postgres, true), "cs").GetAwaiter().GetResult();
        _connectionId = created.Id;
        _ctx.Services.GetRequiredService<AppState>().Select(created);
    }

    [Fact]
    public void Every_live_table_is_listed_including_hidden_ones()
    {
        HideAsync("secrets").GetAwaiter().GetResult();

        var rail = _ctx.RenderComponent<SchemaRail>();

        // The rail is a configuration surface: a hidden table must stay visible here or it could
        // never be restored. Only the schema handed to the agent is filtered.
        Assert.Contains("orders", rail.Markup);
        Assert.Contains("secrets", rail.Markup);
    }

    [Fact]
    public void A_hidden_table_is_rendered_dimmed()
    {
        HideAsync("secrets").GetAwaiter().GetResult();

        var rail = _ctx.RenderComponent<SchemaRail>();

        var row = rail.Find("[data-table='public.secrets']");
        Assert.Contains("hidden", row.ClassList);
    }

    [Fact]
    public void Toggling_the_checkbox_persists_the_new_visibility()
    {
        var rail = _ctx.RenderComponent<SchemaRail>();

        rail.Find("[data-table='public.secrets'] input[type=checkbox]").Change(false);

        using var scope = _ctx.Services.CreateScope();
        var policies = scope.ServiceProvider.GetRequiredService<TablePolicyService>();
        var listed = policies.ListAsync(_connectionId).GetAwaiter().GetResult()!;
        Assert.False(listed.Single(t => t.Table == "secrets").IsVisible);
    }

    [Fact]
    public void Search_filters_the_tree_by_table_name()
    {
        var rail = _ctx.RenderComponent<SchemaRail>();

        rail.Find("input[type=search]").Change("secr");

        Assert.DoesNotContain("orders", rail.Markup);
        Assert.Contains("secrets", rail.Markup);
    }

    private async Task HideAsync(string table)
    {
        using var scope = _ctx.Services.CreateScope();
        var policies = scope.ServiceProvider.GetRequiredService<TablePolicyService>();
        await policies.SetVisibilityAsync(_connectionId, "public", table, false);
    }

    public void Dispose()
    {
        _ctx.Dispose();
        _conn.Dispose();
    }
}

file sealed class RailProviderStub : IDatabaseProvider
{
    public DatabaseProviderType ProviderType => DatabaseProviderType.Postgres;
    public Task<ConnectionTestResult> TestConnectionAsync(string cs, CancellationToken ct = default)
        => Task.FromResult(ConnectionTestResult.Ok(null, 0));

    public Task<DatabaseSchema> GetSchemaAsync(string cs, CancellationToken ct = default)
        => Task.FromResult(new DatabaseSchema([
            new SchemaTable("public", "orders",
                [new SchemaColumn("id", "int", false), new SchemaColumn("total", "numeric", true, Precision: 10, Scale: 2)],
                ["id"], [], []),
            new SchemaTable("public", "secrets",
                [new SchemaColumn("token", "text", false)], [], [], []),
        ]));

    public Task<QueryResultSet> ExecuteQueryAsync(string cs, string sql, QueryExecutionOptions o, CancellationToken ct = default)
        => Task.FromResult(new QueryResultSet([], [], false));
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/SqlAgent.Tests/SqlAgent.Tests.csproj --filter SchemaRailTests`
Expected: FAIL to compile — `SchemaRail` does not exist.

- [ ] **Step 3: Implement the rail**

`src/SqlAgent.Host/Components/Layout/SchemaRail.razor`:

```razor
@rendermode InteractiveServer
@implements IDisposable
@inject ScopedRunner Runner
@inject AppState State

<aside class="rail">
    <label class="label">Connection</label>
    <select @onchange="OnConnectionChanged">
        <option value="">— select —</option>
        @foreach (var c in _connections)
        {
            <option value="@c.Id" selected="@(c.Id == State.ConnectionId)">@c.Name</option>
        }
    </select>

    @if (State.Connection is { } active)
    {
        <p class="meta">@active.ProviderType · @(active.IsReadOnly ? "read-only" : "read-write")</p>

        <label class="label">Schema</label>
        <input type="search" placeholder="Filter tables" value="@_filter" @onchange="OnFilterChanged" />

        <ul class="tree">
            @foreach (var t in Filtered())
            {
                <li data-table="@($"{t.Schema}.{t.Table}")" class="@(t.IsVisible ? "" : "hidden")">
                    <input type="checkbox" checked="@t.IsVisible"
                           @onchange="e => ToggleAsync(t, (bool)(e.Value ?? false))" />
                    <span>@($"{t.Schema}.{t.Table}")</span>
                </li>
            }
        </ul>
    }
</aside>

@code {
    private IReadOnlyList<DatabaseConnectionInfo> _connections = [];
    private IReadOnlyList<TableVisibility> _tables = [];
    private string _filter = "";

    protected override async Task OnInitializedAsync()
    {
        State.Changed += OnStateChanged;
        _connections = await Runner.RunAsync<DatabaseConnectionService, IReadOnlyList<DatabaseConnectionInfo>>(
            s => s.ListAsync());
        await LoadTablesAsync();
    }

    // A method rather than an inline lambda: a lambda assigning a string literal inside a Razor
    // attribute needs escaped quotes, which the Razor parser handles badly.
    private void OnFilterChanged(ChangeEventArgs e) => _filter = e.Value?.ToString() ?? string.Empty;

    private IEnumerable<TableVisibility> Filtered() =>
        string.IsNullOrWhiteSpace(_filter)
            ? _tables
            : _tables.Where(t => t.Table.Contains(_filter, StringComparison.OrdinalIgnoreCase));

    private async Task LoadTablesAsync()
    {
        if (State.ConnectionId is not { } id) { _tables = []; return; }
        // ListAsync returns every live table with its effective visibility — including hidden ones,
        // which SchemaService deliberately omits. The rail needs them to offer the toggle back.
        _tables = await Runner.RunAsync<TablePolicyService, IReadOnlyList<TableVisibility>?>(
            s => s.ListAsync(id)) ?? [];
    }

    private async Task OnConnectionChanged(ChangeEventArgs e)
    {
        var raw = e.Value?.ToString();
        State.Select(Guid.TryParse(raw, out var id) ? _connections.FirstOrDefault(c => c.Id == id) : null);
        await LoadTablesAsync();
    }

    private async Task ToggleAsync(TableVisibility table, bool isVisible)
    {
        if (State.ConnectionId is not { } id) return;
        // SetVisibilityAsync clears the cached schema itself, so the next agent read re-extracts
        // under the new policy; the rail only has to re-read its own list.
        await Runner.RunAsync<TablePolicyService>(
            s => s.SetVisibilityAsync(id, table.Schema, table.Table, isVisible));
        await LoadTablesAsync();
    }

    private void OnStateChanged() => InvokeAsync(StateHasChanged);

    public void Dispose() => State.Changed -= OnStateChanged;
}
```

- [ ] **Step 4: Write the failing error-boundary test**

`tests/SqlAgent.Tests/WorkAreaBoundaryTests.cs`:

```csharp
using Bunit;
using Microsoft.AspNetCore.Components;
using SqlAgent.Host.Components.Shared;

namespace SqlAgent.Tests;

/// <summary>
/// An unexpected exception must not take the page down with it, and must not leak its message —
/// exception text can contain a connection string.
/// </summary>
public class WorkAreaBoundaryTests
{
    [Fact]
    public void An_exception_inside_the_work_area_renders_a_retry_prompt()
    {
        using var ctx = new Bunit.TestContext();

        var area = ctx.RenderComponent<WorkArea>(p => p.AddChildContent<ThrowingChild>());

        Assert.Contains("Something went wrong", area.Markup);
    }

    [Fact]
    public void The_exception_message_is_never_rendered()
    {
        using var ctx = new Bunit.TestContext();

        var area = ctx.RenderComponent<WorkArea>(p => p.AddChildContent<ThrowingChild>());

        Assert.DoesNotContain(ThrowingChild.SecretText, area.Markup);
    }
}

/// <summary>Stands in for a component that fails mid-render with a message that must not escape.</summary>
file sealed class ThrowingChild : ComponentBase
{
    public const string SecretText = "Password=super-secret";

    protected override void OnParametersSet() => throw new InvalidOperationException(SecretText);
}
```

- [ ] **Step 5: Run the test to verify it fails**

Run: `dotnet test tests/SqlAgent.Tests/SqlAgent.Tests.csproj --filter WorkAreaBoundaryTests`
Expected: FAIL to compile — `WorkArea` does not exist.

- [ ] **Step 6: Implement `WorkArea`**

`src/SqlAgent.Host/Components/Shared/WorkArea.razor`:

```razor
<ErrorBoundary>
    <ChildContent>@ChildContent</ChildContent>
    <ErrorContent>
        <div class="outcome" role="alert">
            <p>Something went wrong. Try the action again — the details are in the server log.</p>
        </div>
    </ErrorContent>
</ErrorBoundary>

@code {
    // ErrorContent deliberately ignores the exception argument: its message can carry a connection
    // string. Blazor already logs the exception server-side.
    [Parameter] public RenderFragment? ChildContent { get; set; }
}
```

- [ ] **Step 7: Mount the rail and the boundary in the layout**

Replace `src/SqlAgent.Host/Components/Layout/MainLayout.razor`:

```razor
@inherits LayoutComponentBase

<header>
    <strong>SQL Agent</strong>
    <nav><a href="/">Workspace</a> <a href="/connections">Connections</a></nav>
</header>
<div class="shell">
    <SchemaRail />
    <main>
        <WorkArea>@Body</WorkArea>
    </main>
</div>
```

The boundary wraps only `@Body`, not the rail: a failed page should leave the connection picker and the schema tree usable so the user can switch away from whatever broke.

- [ ] **Step 8: Run the tests to verify they pass**

Run: `dotnet test tests/SqlAgent.Tests/SqlAgent.Tests.csproj --filter "SchemaRailTests|WorkAreaBoundaryTests"`
Expected: PASS, 6 tests.

- [ ] **Step 9: Commit**

```bash
git add -A
git commit -m "Add the schema rail with visibility toggles and a work-area error boundary"
```

---

## Task 7: SQL tab — run, results grid, cancel

The editor is a plain `textarea` here; Task 9 upgrades it to CodeMirror. Splitting keeps the JS interop out of the way while the execution path is proven.

**Files:**
- Create: `src/SqlAgent.Host/Components/Shared/ResultGrid.razor`, `src/SqlAgent.Host/Components/Shared/OutcomeMessage.razor`
- Modify: `src/SqlAgent.Host/Components/Pages/Workspace.razor`
- Test: `tests/SqlAgent.Tests/ResultGridTests.cs`

**Interfaces:**
- Consumes: `QueryExecutionService.ExecuteSqlAsync(Guid, string, CancellationToken) → Task<QueryExecutionResult>`.
- Produces:
  - `ResultGrid` with parameter `QueryExecutionResult? Result`
  - `OutcomeMessage` with parameters `string? Code`, `string? Message`

- [ ] **Step 1: Write the failing test**

`tests/SqlAgent.Tests/ResultGridTests.cs`:

```csharp
using Bunit;
using SqlAgent.Core;
using SqlAgent.Host.Components.Shared;

namespace SqlAgent.Tests;

public class ResultGridTests
{
    private static QueryExecutionResult Success(bool truncated) => QueryExecutionResult.Ok(
        "SELECT 1",
        new QueryResultSet(["id", "name"], [new object?[] { 1, "a" }, new object?[] { 2, null }], truncated),
        18);

    [Fact]
    public void Rows_and_columns_are_rendered()
    {
        using var ctx = new Bunit.TestContext();

        var grid = ctx.RenderComponent<ResultGrid>(p => p.Add(g => g.Result, Success(truncated: false)));

        Assert.Contains("id", grid.Markup);
        Assert.Contains("name", grid.Markup);
        Assert.Equal(2, grid.FindAll("tbody tr").Count);
    }

    [Fact]
    public void A_null_value_renders_as_NULL_not_as_an_empty_cell()
    {
        using var ctx = new Bunit.TestContext();

        var grid = ctx.RenderComponent<ResultGrid>(p => p.Add(g => g.Result, Success(truncated: false)));

        Assert.Contains("NULL", grid.Markup);
    }

    [Fact]
    public void Row_count_and_elapsed_time_are_shown()
    {
        using var ctx = new Bunit.TestContext();

        var grid = ctx.RenderComponent<ResultGrid>(p => p.Add(g => g.Result, Success(truncated: false)));

        Assert.Contains("2 rows", grid.Markup);
        Assert.Contains("18 ms", grid.Markup);
    }

    [Fact]
    public void Truncation_is_announced()
    {
        using var ctx = new Bunit.TestContext();

        var grid = ctx.RenderComponent<ResultGrid>(p => p.Add(g => g.Result, Success(truncated: true)));

        // A capped result is a normal outcome, not an error — but the user must know rows are missing.
        Assert.Contains("truncated", grid.Markup, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_policy_denial_renders_as_a_message_with_its_code()
    {
        using var ctx = new Bunit.TestContext();

        var message = ctx.RenderComponent<OutcomeMessage>(p => p
            .Add(m => m.Code, "policy_denied_readonly")
            .Add(m => m.Message, "Connection is read-only; 'UPDATE' would modify data."));

        Assert.Contains("policy_denied_readonly", message.Markup);
        Assert.Contains("read-only", message.Markup);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/SqlAgent.Tests/SqlAgent.Tests.csproj --filter ResultGridTests`
Expected: FAIL to compile — `ResultGrid` and `OutcomeMessage` do not exist.

- [ ] **Step 3: Implement `OutcomeMessage`**

`src/SqlAgent.Host/Components/Shared/OutcomeMessage.razor`:

```razor
<div class="outcome" role="status">
    <p>@Message</p>
    @if (!string.IsNullOrEmpty(Code))
    {
        <code class="outcome-code">@Code</code>
    }
</div>

@code {
    /// <summary>Stable error code from Core. Shown so a denial reads as deliberate, not as a crash.</summary>
    [Parameter] public string? Code { get; set; }
    [Parameter] public string? Message { get; set; }
}
```

- [ ] **Step 4: Implement `ResultGrid`**

`src/SqlAgent.Host/Components/Shared/ResultGrid.razor`:

```razor
@if (Result is { Success: true } r)
{
    <p class="meta">
        @r.RowCount rows · @r.ElapsedMs ms
        @if (r.Truncated)
        {
            <strong> · results truncated at the row cap</strong>
        }
    </p>

    <div class="grid-scroll">
        <table>
            <thead>
                <tr>@foreach (var c in r.Columns) { <th>@c</th> }</tr>
            </thead>
            <tbody>
                @foreach (var row in r.Rows)
                {
                    <tr>
                        @foreach (var value in row)
                        {
                            <td>@(value is null ? "NULL" : value.ToString())</td>
                        }
                    </tr>
                }
            </tbody>
        </table>
    </div>
}
else if (Result is { } failed)
{
    <OutcomeMessage Code="@failed.ErrorCode" Message="@failed.ErrorMessage" />
}

@code {
    [Parameter] public QueryExecutionResult? Result { get; set; }
}
```

- [ ] **Step 5: Implement the SQL tab in `Workspace.razor`**

```razor
@page "/"
@rendermode InteractiveServer
@inject ScopedRunner Runner
@inject AppState State

<div class="tabs">
    <button class="@(_tab == Tab.Sql ? "active" : "")" @onclick="() => _tab = Tab.Sql">SQL</button>
    <button class="@(_tab == Tab.Chat ? "active" : "")" @onclick="() => _tab = Tab.Chat">Chat</button>
</div>

@if (State.ConnectionId is null)
{
    <p>Select a connection to start querying.</p>
}
else if (_tab == Tab.Sql)
{
    <textarea rows="8" @bind="_sql" @bind:event="oninput" placeholder="SELECT ..."></textarea>

    <div class="actions">
        <button @onclick="RunAsync" disabled="@(_running || string.IsNullOrWhiteSpace(_sql))">Run</button>
        @if (_running)
        {
            <button @onclick="Cancel">Cancel</button>
        }
    </div>

    <ResultGrid Result="_result" />
}

@code {
    private enum Tab { Sql, Chat }

    private Tab _tab = Tab.Sql;
    private string _sql = "";
    private bool _running;
    private QueryExecutionResult? _result;
    private CancellationTokenSource? _cts;

    private async Task RunAsync()
    {
        if (State.ConnectionId is not { } id) return;
        _running = true;
        _result = null;
        _cts = new CancellationTokenSource();
        try
        {
            // ExecuteSqlAsync validates policy, enforces the timeout and row cap, and writes the audit
            // row itself. Cancelling here surfaces as execution_canceled, distinct from execution_timeout.
            _result = await Runner.RunAsync<QueryExecutionService, QueryExecutionResult>(
                s => s.ExecuteSqlAsync(id, _sql, _cts.Token));
        }
        finally
        {
            _running = false;
            _cts.Dispose();
            _cts = null;
        }
    }

    private void Cancel() => _cts?.Cancel();
}
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test tests/SqlAgent.Tests/SqlAgent.Tests.csproj --filter ResultGridTests`
Expected: PASS, 5 tests.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "Add the SQL tab with a result grid and cancellation"
```

---

## Task 8: CSV and JSON export

**Files:**
- Create: `src/SqlAgent.Host/Web/ResultExport.cs`
- Modify: `src/SqlAgent.Host/Components/Shared/ResultGrid.razor`
- Test: `tests/SqlAgent.Tests/ResultExportTests.cs`

**Interfaces:**
- Produces: `ResultExport.ToCsv(IReadOnlyList<string>, IReadOnlyList<IReadOnlyList<object?>>) → string`, `ResultExport.ToJson(...) → string`.

- [ ] **Step 1: Write the failing test**

`tests/SqlAgent.Tests/ResultExportTests.cs`:

```csharp
using SqlAgent.Host.Web;

namespace SqlAgent.Tests;

public class ResultExportTests
{
    [Fact]
    public void Csv_writes_a_header_row_and_one_line_per_row()
    {
        var csv = ResultExport.ToCsv(["id", "name"], [new object?[] { 1, "a" }, new object?[] { 2, "b" }]);

        Assert.Equal("id,name\r\n1,a\r\n2,b\r\n", csv);
    }

    [Fact]
    public void Csv_quotes_values_containing_a_comma_quote_or_newline()
    {
        var csv = ResultExport.ToCsv(["v"], [
            new object?[] { "a,b" },
            new object?[] { "say \"hi\"" },
            new object?[] { "line1\nline2" },
        ]);

        Assert.Contains("\"a,b\"", csv);
        Assert.Contains("\"say \"\"hi\"\"\"", csv);   // a quote is escaped by doubling it
        Assert.Contains("\"line1\nline2\"", csv);
    }

    [Fact]
    public void Csv_writes_null_as_an_empty_field_not_the_text_NULL()
    {
        // The grid shows NULL for readability; a CSV consumer expects an empty field.
        var csv = ResultExport.ToCsv(["a", "b"], [new object?[] { null, "" }]);

        Assert.Equal("a,b\r\n,\r\n", csv);
    }

    [Fact]
    public void Json_writes_an_array_of_objects_keyed_by_column()
    {
        var json = ResultExport.ToJson(["id", "name"], [new object?[] { 1, "a" }]);

        Assert.Equal("""[{"id":1,"name":"a"}]""", json);
    }

    [Fact]
    public void Json_preserves_null_as_null()
    {
        var json = ResultExport.ToJson(["id"], [new object?[] { null }]);

        Assert.Equal("""[{"id":null}]""", json);
    }

    [Fact]
    public void Duplicate_column_names_are_disambiguated_so_no_value_is_lost()
    {
        // A projection may legitimately produce two columns of the same name; a JSON object cannot
        // hold two identical keys, so the second occurrence is suffixed rather than silently dropped.
        var json = ResultExport.ToJson(["id", "id"], [new object?[] { 1, 2 }]);

        Assert.Equal("""[{"id":1,"id (2)":2}]""", json);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/SqlAgent.Tests/SqlAgent.Tests.csproj --filter ResultExportTests`
Expected: FAIL to compile — `ResultExport` does not exist.

- [ ] **Step 3: Implement `ResultExport`**

`src/SqlAgent.Host/Web/ResultExport.cs`:

```csharp
using System.Text;
using System.Text.Json;

namespace SqlAgent.Host.Web;

/// <summary>
/// Serializes an already-fetched result set. Export never re-queries: the user downloads exactly the
/// rows they were shown, and no second audit entry appears for a button that only formats data.
/// </summary>
public static class ResultExport
{
    public static string ToCsv(IReadOnlyList<string> columns, IReadOnlyList<IReadOnlyList<object?>> rows)
    {
        var sb = new StringBuilder();
        sb.Append(string.Join(',', columns.Select(Escape))).Append("\r\n");
        foreach (var row in rows)
            sb.Append(string.Join(',', row.Select(v => Escape(v?.ToString())))).Append("\r\n");
        return sb.ToString();
    }

    public static string ToJson(IReadOnlyList<string> columns, IReadOnlyList<IReadOnlyList<object?>> rows)
    {
        var names = Disambiguate(columns);
        var objects = rows.Select(row => names
            .Select((name, i) => (name, value: i < row.Count ? row[i] : null))
            .ToDictionary(p => p.name, p => p.value));
        return JsonSerializer.Serialize(objects);
    }

    /// <summary>A null becomes an empty field; only comma, quote, or newline force quoting.</summary>
    private static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        if (!value.AsSpan().ContainsAny(',', '"', '\n', '\r')) return value;
        return $"\"{value.Replace("\"", "\"\"")}\"";
    }

    /// <summary>A JSON object cannot hold duplicate keys, so repeats get a " (n)" suffix.</summary>
    private static List<string> Disambiguate(IReadOnlyList<string> columns)
    {
        var seen = new Dictionary<string, int>();
        var result = new List<string>(columns.Count);
        foreach (var raw in columns)
        {
            var name = string.IsNullOrEmpty(raw) ? "(column)" : raw;
            if (seen.TryGetValue(name, out var n))
            {
                seen[name] = n + 1;
                name = $"{name} ({n + 1})";
            }
            else
            {
                seen[name] = 1;
            }
            result.Add(name);
        }
        return result;
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/SqlAgent.Tests/SqlAgent.Tests.csproj --filter ResultExportTests`
Expected: PASS, 6 tests.

- [ ] **Step 5: Add the download buttons to `ResultGrid.razor`**

Add `@inject IJSRuntime JS` at the top, and inside the `Result is { Success: true } r` branch, above `<div class="grid-scroll">`:

```razor
    <div class="actions">
        <button @onclick="DownloadCsvAsync">Export CSV</button>
        <button @onclick="DownloadJsonAsync">Export JSON</button>
    </div>
```

And in `@code`. Two named methods rather than `@onclick="() => DownloadAsync("csv")"`: a string literal inside a Razor attribute would need escaped quotes, which the Razor parser handles badly.

```csharp
    private Task DownloadCsvAsync() => DownloadAsync("csv");
    private Task DownloadJsonAsync() => DownloadAsync("json");

    private async Task DownloadAsync(string format)
    {
        if (Result is not { Success: true } r) return;
        var content = format == "csv"
            ? ResultExport.ToCsv(r.Columns, r.Rows)
            : ResultExport.ToJson(r.Columns, r.Rows);
        var mime = format == "csv" ? "text/csv" : "application/json";
        await JS.InvokeVoidAsync("sqlAgentDownload", $"result.{format}", mime, content);
    }
```

- [ ] **Step 6: Add the download helper**

`src/SqlAgent.Host/wwwroot/js/download.js`:

```javascript
// Turns an in-memory string into a file download. Kept in JS because a browser cannot be handed
// bytes from .NET without either a blob URL or a round trip through the server.
window.sqlAgentDownload = (filename, mimeType, content) => {
  const url = URL.createObjectURL(new Blob([content], { type: mimeType }));
  const link = document.createElement('a');
  link.href = url;
  link.download = filename;
  link.click();
  URL.revokeObjectURL(url);
};
```

Reference it in `Components/App.razor`, before the `blazor.web.js` tag:

```html
    <script src="js/download.js"></script>
```

- [ ] **Step 7: Verify the whole suite passes**

Run: `dotnet test SqlAgent.slnx -c Release`
Expected: PASS.

- [ ] **Step 8: Commit**

```bash
git add -A
git commit -m "Add CSV and JSON export for query results"
```

---

## Task 9: CodeMirror SQL editor

**Files:**
- Create: `src/SqlAgent.Host/wwwroot/lib/codemirror/codemirror.min.js`, `codemirror.min.css`, `sql.min.js`
- Create: `src/SqlAgent.Host/wwwroot/js/sql-editor.js`, `src/SqlAgent.Host/Components/Shared/SqlEditor.razor`
- Modify: `src/SqlAgent.Host/Components/Pages/Workspace.razor`, `src/SqlAgent.Host/Components/App.razor`

**Interfaces:**
- Produces: `SqlEditor` with parameters `string Value`, `EventCallback<string> ValueChanged`, `EventCallback OnRun`.

- [ ] **Step 1: Vendor CodeMirror 5**

Download these three files into `src/SqlAgent.Host/wwwroot/lib/codemirror/`:

```bash
mkdir -p src/SqlAgent.Host/wwwroot/lib/codemirror
curl -L -o src/SqlAgent.Host/wwwroot/lib/codemirror/codemirror.min.js  https://cdnjs.cloudflare.com/ajax/libs/codemirror/5.65.16/codemirror.min.js
curl -L -o src/SqlAgent.Host/wwwroot/lib/codemirror/codemirror.min.css https://cdnjs.cloudflare.com/ajax/libs/codemirror/5.65.16/codemirror.min.css
curl -L -o src/SqlAgent.Host/wwwroot/lib/codemirror/sql.min.js         https://cdnjs.cloudflare.com/ajax/libs/codemirror/5.65.16/mode/sql/sql.min.js
```

Version 5, not 6, and committed to the repository rather than loaded from the CDN at runtime. CodeMirror 6 ships ES modules that need a bundler, which would put Node in the build — the cost this whole stack choice exists to avoid. Loading from a CDN would also break a local agent that is offline and would leak usage to a third party.

- [ ] **Step 2: Write the interop**

`src/SqlAgent.Host/wwwroot/js/sql-editor.js`:

```javascript
// Bridges CodeMirror to the Blazor component. The editor owns the text while it is focused; .NET is
// notified on every change so @bind-style flow still works, and Ctrl+Enter runs the query.
window.sqlAgentEditor = {
  create: (element, dotNetRef, initialValue) => {
    const editor = CodeMirror(element, {
      value: initialValue || '',
      mode: 'text/x-sql',
      lineNumbers: true,
      viewportMargin: Infinity,
      extraKeys: {
        'Ctrl-Enter': () => dotNetRef.invokeMethodAsync('RunFromEditor'),
        'Cmd-Enter': () => dotNetRef.invokeMethodAsync('RunFromEditor'),
      },
    });
    editor.on('change', () => dotNetRef.invokeMethodAsync('OnEditorChanged', editor.getValue()));
    element._cm = editor;
  },
  setValue: (element, value) => {
    const editor = element._cm;
    if (editor && editor.getValue() !== value) editor.setValue(value || '');
  },
};
```

- [ ] **Step 3: Write the component**

`src/SqlAgent.Host/Components/Shared/SqlEditor.razor`:

```razor
@rendermode InteractiveServer
@implements IAsyncDisposable
@inject IJSRuntime JS

<div class="editor" @ref="_host"></div>

@code {
    private ElementReference _host;
    private DotNetObjectReference<SqlEditor>? _self;
    private string _lastPushed = "";

    [Parameter] public string Value { get; set; } = "";
    [Parameter] public EventCallback<string> ValueChanged { get; set; }
    [Parameter] public EventCallback OnRun { get; set; }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            _self = DotNetObjectReference.Create(this);
            await JS.InvokeVoidAsync("sqlAgentEditor.create", _host, _self, Value);
            _lastPushed = Value;
        }
        else if (Value != _lastPushed)
        {
            // Only push when the parent changed the text (e.g. "open in editor" from the chat tab);
            // echoing the user's own keystrokes back would move the caret.
            await JS.InvokeVoidAsync("sqlAgentEditor.setValue", _host, Value);
            _lastPushed = Value;
        }
    }

    [JSInvokable]
    public async Task OnEditorChanged(string value)
    {
        _lastPushed = value;
        Value = value;
        await ValueChanged.InvokeAsync(value);
    }

    [JSInvokable]
    public async Task RunFromEditor() => await OnRun.InvokeAsync();

    public async ValueTask DisposeAsync()
    {
        _self?.Dispose();
        await ValueTask.CompletedTask;
    }
}
```

- [ ] **Step 4: Reference the assets in `App.razor`**

In `<head>`:

```html
    <link rel="stylesheet" href="lib/codemirror/codemirror.min.css" />
```

Before `blazor.web.js`:

```html
    <script src="lib/codemirror/codemirror.min.js"></script>
    <script src="lib/codemirror/sql.min.js"></script>
    <script src="js/sql-editor.js"></script>
```

- [ ] **Step 5: Swap the textarea for the editor in `Workspace.razor`**

Replace the `<textarea ...></textarea>` line with:

```razor
    <SqlEditor @bind-Value="_sql" OnRun="RunAsync" />
```

- [ ] **Step 6: Verify build and suite**

Run: `dotnet build SqlAgent.slnx -c Release && dotnet test SqlAgent.slnx -c Release`
Expected: build succeeds; all tests pass. The editor itself is verified manually in Task 11 — bUnit has no JavaScript engine, so a CodeMirror instance cannot be asserted on in a component test.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "Replace the SQL textarea with a vendored CodeMirror editor"
```

---

## Task 10: Chat tab

**Files:**
- Modify: `src/SqlAgent.Host/Components/Pages/Workspace.razor`
- Test: `tests/SqlAgent.Tests/WorkspaceChatTests.cs`

**Interfaces:**
- Consumes: `NlQueryService.AskAsync(Guid, string, CancellationToken) → Task<NlQueryResult>`, `NlResponseKind`.

- [ ] **Step 1: Write the failing test**

`tests/SqlAgent.Tests/WorkspaceChatTests.cs`:

```csharp
using Bunit;
using SqlAgent.Host.Components.Shared;
using SqlAgent.Storage;

namespace SqlAgent.Tests;

/// <summary>
/// The three ask_database outcomes render differently, and llm_error is special: with no provider wired
/// it is the only outcome the user will ever see, so it must read as "not configured" rather than as a
/// failure of their question.
/// </summary>
public class WorkspaceChatTests
{
    [Fact]
    public void An_llm_error_is_explained_rather_than_shown_as_a_raw_code()
    {
        using var ctx = new Bunit.TestContext();
        var result = NlQueryResult.Error("llm_error", "No LLM provider is configured on this server.");

        var view = ctx.RenderComponent<ChatOutcome>(p => p.Add(c => c.Result, result));

        Assert.Contains("LLM is not configured", view.Markup);
        Assert.DoesNotContain("llm_error", view.Markup);
    }

    [Fact]
    public void A_clarification_shows_the_question_and_no_sql()
    {
        using var ctx = new Bunit.TestContext();
        var result = NlQueryResult.Clarification("Which year did you mean?");

        var view = ctx.RenderComponent<ChatOutcome>(p => p.Add(c => c.Result, result));

        Assert.Contains("Which year did you mean?", view.Markup);
        Assert.DoesNotContain("<pre", view.Markup);
    }

    [Fact]
    public void A_rejected_query_still_shows_the_generated_sql()
    {
        using var ctx = new Bunit.TestContext();
        var result = NlQueryResult.Error(
            "policy_denied_hidden_table", "Query references a hidden table.", "SELECT * FROM secrets");

        var view = ctx.RenderComponent<ChatOutcome>(p => p.Add(c => c.Result, result));

        // Auditability: the user must be able to see what was generated and why it was refused.
        Assert.Contains("SELECT * FROM secrets", view.Markup);
        Assert.Contains("policy_denied_hidden_table", view.Markup);
    }

    [Fact]
    public void A_successful_answer_shows_the_generated_sql_and_the_rows()
    {
        using var ctx = new Bunit.TestContext();
        var result = new NlQueryResult(
            NlResponseKind.QueryResult, "SELECT count(*) FROM orders", null, null, null,
            ["count"], [new object?[] { 42 }], 1, false, 7);

        var view = ctx.RenderComponent<ChatOutcome>(p => p.Add(c => c.Result, result));

        Assert.Contains("SELECT count(*) FROM orders", view.Markup);
        Assert.Contains("42", view.Markup);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/SqlAgent.Tests/SqlAgent.Tests.csproj --filter WorkspaceChatTests`
Expected: FAIL to compile — `ChatOutcome` does not exist.

- [ ] **Step 3: Implement `ChatOutcome`**

`src/SqlAgent.Host/Components/Shared/ChatOutcome.razor`:

```razor
@if (Result is { } r)
{
    @if (!string.IsNullOrEmpty(r.GeneratedSql))
    {
        <pre class="generated-sql">@r.GeneratedSql</pre>
        <button @onclick="() => OnOpenInEditor.InvokeAsync(r.GeneratedSql)">Open in editor</button>
    }

    @switch (r.Kind)
    {
        case NlResponseKind.QueryResult:
            <p class="meta">@r.RowCount rows · @r.ElapsedMs ms @(r.Truncated ? "· results truncated" : "")</p>
            <div class="grid-scroll">
                <table>
                    <thead><tr>@foreach (var c in r.Columns) { <th>@c</th> }</tr></thead>
                    <tbody>
                        @foreach (var row in r.Rows)
                        {
                            <tr>@foreach (var v in row) { <td>@(v is null ? "NULL" : v.ToString())</td> }</tr>
                        }
                    </tbody>
                </table>
            </div>
            break;

        case NlResponseKind.ClarificationRequired:
            <p class="clarification">@r.ClarificationQuestion</p>
            break;

        case NlResponseKind.Error when r.ErrorCode == "llm_error":
            // No provider is wired yet, so this is the only outcome a user can currently reach.
            // Showing the bare code would read as a bug in their question.
            <div class="outcome" role="status">
                <p>The LLM is not configured on this server, so natural-language questions cannot be
                   answered yet. Use the SQL tab, or see <code>docs/runbook.md</code> to configure a provider.</p>
            </div>
            break;

        case NlResponseKind.Error:
            <OutcomeMessage Code="@r.ErrorCode" Message="@r.ErrorMessage" />
            break;
    }
}

@code {
    [Parameter] public NlQueryResult? Result { get; set; }
    [Parameter] public EventCallback<string> OnOpenInEditor { get; set; }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/SqlAgent.Tests/SqlAgent.Tests.csproj --filter WorkspaceChatTests`
Expected: PASS, 4 tests.

- [ ] **Step 5: Wire the chat tab into `Workspace.razor`**

Add after the `_tab == Tab.Sql` block:

```razor
else if (_tab == Tab.Chat)
{
    <div class="transcript">
        @foreach (var entry in _transcript)
        {
            <p class="question">@entry.Question</p>
            <ChatOutcome Result="entry.Result" OnOpenInEditor="OpenInEditor" />
        }
    </div>

    <input @bind="_question" @bind:event="oninput" placeholder="Ask a question about this database" />
    <button @onclick="AskAsync" disabled="@(_asking || string.IsNullOrWhiteSpace(_question))">Ask</button>
}
```

And in `@code`:

```csharp
    private record TranscriptEntry(string Question, NlQueryResult Result);

    private readonly List<TranscriptEntry> _transcript = [];
    private string _question = "";
    private bool _asking;

    private async Task AskAsync()
    {
        if (State.ConnectionId is not { } id) return;
        _asking = true;
        var question = _question;
        _question = "";
        try
        {
            // AskAsync runs generated SQL through the same validate-then-execute path as the SQL tab,
            // so policy still applies to whatever the model produced.
            var result = await Runner.RunAsync<NlQueryService, NlQueryResult>(s => s.AskAsync(id, question));
            _transcript.Add(new TranscriptEntry(question, result));
        }
        finally
        {
            _asking = false;
        }
    }

    private void OpenInEditor(string sql)
    {
        _sql = sql;
        _tab = Tab.Sql;
    }
```

- [ ] **Step 6: Verify the whole suite passes**

Run: `dotnet test SqlAgent.slnx -c Release`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "Add the natural-language chat tab"
```

---

## Task 11: Documentation, packaging, and manual verification

**Files:**
- Create: `docs/web-ui.md`
- Delete: `docs/wpf-client.md`
- Modify: `README.md`, `docs/runbook.md`, `packaging/windows/install-service.ps1`, `packaging/systemd/sqlagent.service`

- [ ] **Step 1: Manually verify the running app**

```bash
dotnet run --project src/SqlAgent.Host/SqlAgent.Host.csproj
```

Open the URL the host logs, including its `?token=`, and walk this list. Record the result of each line in the commit message.

| Check | Expected |
|---|---|
| Open the logged URL | Workspace loads |
| Open `http://127.0.0.1:5099/` with no token in a private window | 401 |
| Create a connection, then test it | Version and elapsed time reported |
| Reopen the connection for editing | Connection-string field is empty |
| Select the connection | Rail lists tables with checkboxes |
| Uncheck a table, run `SELECT` against it | `policy_denied_hidden_table` |
| Set read-only, run an `UPDATE` | `policy_denied_readonly` |
| Type SQL, press Ctrl+Enter | Query runs, syntax is highlighted |
| Run a query returning more than 1000 rows | Truncation notice appears |
| Export CSV, then JSON | Both files download and open cleanly |
| Ask a question on the Chat tab | "LLM is not configured" explanation, not a raw code |
| Start a slow query, press Cancel | `execution_canceled` |

- [ ] **Step 2: Write `docs/web-ui.md`**

Cover: how to start the host, the logged URL and token, the `SqlAgent:Web:Port` setting with its 5099 default, the loopback-only binding and why, the three screens, the export behavior, and the fact that natural-language chat needs an LLM provider that is not yet wired. Include the manual checklist from Step 1 as the regression list for changes that bUnit cannot cover, and state plainly that the CodeMirror editor is only verified this way.

- [ ] **Step 3: Update `README.md`**

Replace the WPF paragraph with instructions to run the host and open the logged URL. Point at `docs/web-ui.md`.

- [ ] **Step 4: Update `docs/runbook.md`**

Add a "Web UI" section: the URL, `SqlAgent:Web:Port`, the launch token, and the distinction between a configured `SqlAgent:LocalAuth:Token` (persisted, shared with the MCP server) and the generated per-start token (in memory, printed at startup). Add a troubleshooting line for a 401 caused by opening the bare URL without the token.

- [ ] **Step 5: Add the port to the packaging scripts**

In `packaging/systemd/sqlagent.service`, add one line to the `[Service]` section, next to the existing
`Environment=` lines:

```ini
Environment=SqlAgent__Web__Port=5099
```

In `packaging/windows/install-service.ps1`, add the port parameter and write it into the service
environment. `New-Service` cannot set environment variables, so it goes into the registry key the
Service Control Manager reads for that service:

```powershell
param(
    [string]$PublishPath = "C:\Program Files\SqlAgent",
    [string]$ServiceName = "SqlAgent",
    [string]$DisplayName = "SQL Agent",
    [int]$Port = 5099
)

$exe = Join-Path $PublishPath "SqlAgent.Host.exe"
if (-not (Test-Path $exe)) {
    throw "Host executable not found at $exe. Publish the host before installing the service."
}

New-Service `
    -Name $ServiceName `
    -DisplayName $DisplayName `
    -BinaryPathName "`"$exe`"" `
    -StartupType Automatic

# New-Service has no environment switch; the SCM reads this multi-string value when it starts the service.
Set-ItemProperty `
    -Path "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName" `
    -Name Environment `
    -Type MultiString `
    -Value @("SqlAgent__Web__Port=$Port")

Start-Service -Name $ServiceName

Write-Host "SQL Agent UI will listen on http://127.0.0.1:$Port — the launch token is written to the service log."
```

- [ ] **Step 6: Delete the obsolete doc**

```bash
git rm docs/wpf-client.md
```

- [ ] **Step 7: Confirm no stale references remain**

Run: `grep -rn "WPF\|named pipe\|NamedPipe\|Api.Local" README.md docs/ packaging/ --include="*.md" --include="*.ps1" --include="*.service"`
Expected: no matches outside `docs/adr/` and `docs/superpowers/`, which are historical records and must not be edited.

- [ ] **Step 8: Full verification**

Run: `dotnet build SqlAgent.slnx -c Release && dotnet test SqlAgent.slnx -c Release`
Expected: build succeeds, all tests pass.

- [ ] **Step 9: Commit**

```bash
git add -A
git commit -m "Document the web UI and retire the WPF client docs"
```

---

## Out of scope

Tracked in the spec, not implemented here: a portable secret store replacing DPAPI (phase 2), remote access with TLS and sessions (phase 3), wiring a real LLM provider, and voice input.
