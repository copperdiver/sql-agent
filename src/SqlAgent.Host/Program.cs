using Microsoft.EntityFrameworkCore;
using SqlAgent.Core;
using SqlAgent.Host.Components;
using SqlAgent.Host.Web;
using SqlAgent.Providers.Postgres;
using SqlAgent.Providers.SqlServer;
using SqlAgent.Storage;

var builder = WebApplication.CreateBuilder(args);

// WebApplication.CreateBuilder only wires referenced packages' static web assets (blazor.web.js among
// them) into the static-file provider when the hosting environment is Development. Nothing here sets
// ASPNETCORE_ENVIRONMENT (there is no launchSettings.json, and the Windows-service/systemd hosts run in
// Production deliberately), so without this call the Interactive Server script the whole UI depends on
// 404s under an unpublished `dotnet run` — the exact command the docs tell an operator to use. A real
// `dotnet publish` copies those assets into wwwroot regardless, so this only changes behavior for the
// unpublished case, which is otherwise silently broken.
builder.WebHost.UseStaticWebAssets();

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
builder.Services.AddScoped<ScopedRunner>();
builder.Services.AddScoped<AppState>();

// Fail-closed LLM seam: ask_database resolves to a stable llm_not_configured until a vendor gateway is wired.
builder.Services.AddSingleton<ILlmSqlGateway, UnavailableLlmSqlGateway>();

if (OperatingSystem.IsWindows())
    builder.Services.AddScoped<ISecretStore, DpapiSecretStore>();
else
    builder.Services.AddSingleton<ISecretStore, InMemorySecretStore>();

builder.Services.AddRazorComponents().AddInteractiveServerComponents();

builder.Services.AddSingleton<LaunchToken>();
builder.WebHost.UseUrls(LoopbackUrl.Resolve(builder.Configuration));

var app = builder.Build();

using (var scope = app.Services.CreateScope())
    await scope.ServiceProvider.GetRequiredService<SqlAgentDbContext>().Database.EnsureCreatedAsync();

// Order matters: origin checks run before anything reads the token, so a hostile page cannot even
// attempt an exchange.
app.UseMiddleware<LocalOriginMiddleware>();
app.UseMiddleware<TokenAuthMiddleware>();

app.UseStaticFiles();
app.UseAntiforgery();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

// The token is required only for the first request, which exchanges it for a session cookie. It is
// NOT logged: with the named pipe gone, this token is the entire trust boundary around a TCP port
// that every local account can reach, and the log providers this host attaches are not private to the
// service account — AddWindowsService() writes to the Windows Event Log and AddSystemd() puts stdout
// in the journal. It goes to a file beside the store instead, readable only by this account and local
// administrators. See docs/runbook.md for the retrieval path.
var launchToken = app.Services.GetRequiredService<LaunchToken>();
var launchUrl = LoopbackUrl.Resolve(app.Configuration);
if (launchToken.IsGenerated)
{
    // Only a generated token is written to disk. An operator-configured token is a long-lived secret
    // the operator already holds (it's the same value that unlocks the MCP server) — writing it out
    // too would create a second, indefinitely-lived plaintext copy for no benefit, since unlike a
    // generated value it does not go stale on the next restart.
    try
    {
        var directory = LaunchUrlFile.ResolveDirectory(app.Configuration);
        var launchUrlPath = LaunchUrlFile.Write(directory, $"{launchUrl}/?token={launchToken.Value}");
        app.Logger.LogInformation(
            "SQL Agent UI: {Url} — open the URL (token included) written to {LaunchUrlFile}", launchUrl, launchUrlPath);

        // The file must not outlive the process that created it: it's a live secret at rest for as
        // long as it sits on disk. Best-effort — LaunchUrlFile.Delete never throws, so a locked or
        // already-missing file cannot block shutdown.
        app.Lifetime.ApplicationStopping.Register(() =>
        {
            if (!LaunchUrlFile.Delete(directory))
                app.Logger.LogWarning(
                    "SQL Agent UI: could not remove {LaunchUrlFile} on shutdown; delete it manually.", launchUrlPath);
        });
    }
    catch (Exception ex)
    {
        // Fail soft and stay silent about the value: a read-only deployment directory must not stop the
        // host, and must not tempt this into logging the token as a fallback either. Setting
        // SqlAgent:LocalAuth:Token gives the operator a value they already know.
        app.Logger.LogError(ex,
            "SQL Agent UI: {Url} — the launch URL file could not be written, so the generated token cannot be "
            + "retrieved. Set SqlAgent:LocalAuth:Token to a value of your own, or make {Directory} writable.",
            launchUrl, LaunchUrlFile.ResolveDirectory(app.Configuration));
    }
}
else
{
    // The operator already knows this value — it's the one they configured. Nothing to write, and
    // nothing to disclose here either: just point at the URL and let them append their own token.
    app.Logger.LogInformation(
        "SQL Agent UI: {Url} — open the URL with your configured SqlAgent:LocalAuth:Token appended as ?token=…",
        launchUrl);
}

await app.RunAsync();

/// <summary>Placeholder until a real LLM gateway is wired. Throwing <see cref="NotSupportedException"/> is
/// the documented signal NlQueryService keys off to map this to the stable, user-safe llm_not_configured
/// code (as opposed to llm_error, reserved for a configured provider's own call failures), so ask_database
/// is contract-complete and fails closed rather than half-wired.</summary>
internal sealed class UnavailableLlmSqlGateway : ILlmSqlGateway
{
    public Task<LlmSqlResponse> GenerateSqlAsync(LlmSqlRequest request, CancellationToken ct = default) =>
        throw new NotSupportedException("No LLM provider is configured on this server.");
}

/// <summary>Exposed so integration tests can boot the real pipeline via WebApplicationFactory.</summary>
public partial class Program;
