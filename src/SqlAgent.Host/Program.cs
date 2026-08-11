using Microsoft.EntityFrameworkCore;
using SqlAgent.Core;
using SqlAgent.Host.Components;
using SqlAgent.Host.Web;
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
builder.Services.AddScoped<ScopedRunner>();
builder.Services.AddScoped<AppState>();

// Fail-closed LLM seam: ask_database resolves to a stable llm_error until a vendor gateway is wired.
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

// Print the ready-to-click URL the way Jupyter does: the token is required only for the first request,
// which then exchanges it for a session cookie.
var launchToken = app.Services.GetRequiredService<LaunchToken>();
app.Logger.LogInformation(
    "SQL Agent UI: {Url}/?token={Token}", LoopbackUrl.Resolve(app.Configuration), launchToken.Value);

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
