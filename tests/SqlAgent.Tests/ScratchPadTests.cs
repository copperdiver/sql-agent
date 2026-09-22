using Bunit;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SqlAgent.Core;
using SqlAgent.Host.Components.Shared.Chat;
using SqlAgent.Host.Components.Shared;
using SqlAgent.Host.Web;
using SqlAgent.Storage;

namespace SqlAgent.Tests;

sealed class ScratchProvider : IDatabaseProvider
{
    public bool Executed { get; private set; }
    public DatabaseProviderType ProviderType => DatabaseProviderType.Postgres;
    public Task<ConnectionTestResult> TestConnectionAsync(string cs, CancellationToken ct = default) =>
        Task.FromResult(ConnectionTestResult.Ok(null, 0));
    public Task<DatabaseSchema> GetSchemaAsync(string cs, CancellationToken ct = default) =>
        Task.FromResult(new DatabaseSchema([]));
    public Task<QueryResultSet> ExecuteQueryAsync(string cs, string sql, QueryExecutionOptions options,
        CancellationToken ct = default)
    {
        Executed = true;
        return Task.FromResult(new QueryResultSet(["ok"], [new object?[] { 1 }], false));
    }
}

public class ScratchPadTests : IDisposable
{
    private readonly SqliteConnection _conn = new("DataSource=:memory:");
    private readonly Bunit.TestContext _ctx = new();
    private readonly ScratchProvider _provider = new();

    public ScratchPadTests()
    {
        _conn.Open();
        _ctx.Services.AddDbContext<SqlAgentDbContext>(o => o.UseSqlite(_conn));
        _ctx.Services.AddSingleton<ISecretStore, InMemorySecretStore>();
        _ctx.Services.AddSingleton<IDatabaseProvider>(_provider);
        _ctx.Services.AddSingleton<IDatabaseProviderRegistry, DatabaseProviderRegistry>();
        _ctx.Services.AddScoped<DatabaseConnectionService>();
        _ctx.Services.AddScoped<SchemaService>();
        _ctx.Services.AddScoped<QueryExecutionService>();
        _ctx.Services.AddScoped<ScopedRunner>();
        _ctx.Services.AddLogging();
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        using var scope = _ctx.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<SqlAgentDbContext>().Database.EnsureCreated();
    }

    public void Dispose() { _ctx.Dispose(); _conn.Dispose(); }

    [Fact]
    public async Task Scratchpad_round_trips_sql_and_runs_writes_as_confirmed()
    {
        using var scope = _ctx.Services.CreateScope();
        var id = (await scope.ServiceProvider.GetRequiredService<DatabaseConnectionService>().CreateAsync(
            new DatabaseConnectionInput("prod", DatabaseProviderType.Postgres, false), "cs")).Id;

        var pad = _ctx.RenderComponent<ScratchPad>(p => p
            .Add(x => x.ConnectionId, id)
            .Add(x => x.InitialSql, "UPDATE orders SET total = 0"));

        var editor = pad.FindComponent<SqlEditor>();
        Assert.Equal("UPDATE orders SET total = 0", editor.Instance.Value);
        await editor.InvokeAsync(() => editor.Instance.RunFromEditor());

        Assert.True(_provider.Executed);
        Assert.Contains("ok", pad.Markup);
    }
}
