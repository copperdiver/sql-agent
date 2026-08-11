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
